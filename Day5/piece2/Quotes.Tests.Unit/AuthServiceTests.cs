using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;
using NSubstitute;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

public class AuthServiceTests
{
    // Builds a real AuthService wired to a throwaway EF InMemory database
    // (a fresh, uniquely-named one per call so tests never share state)
    // and an in-memory configuration source, so only the FakeClock is a
    // fake -- everything AuthService actually depends on for
    // GenerateAccessToken/HashPassword behaves exactly as it would in
    // production. LoginAsync itself is deliberately NOT covered here: it's
    // pure orchestration (look up user, verify password, delegate to
    // GenerateAccessToken and IRefreshTokenService) that the integration
    // suite already exercises end-to-end through real HTTP calls.
    internal const string TestIssuer = "https://issuer.under.test";
    internal const string TestAudience = "audience-under-test";

    private static AuthService CreateSut(
        IClock clock,
        JwtOptions? jwtOptions = null)
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new QuotesDbContext(options);

        jwtOptions ??= new JwtOptions
        {
            Secret = "unit-test-secret-that-is-long-enough-for-hmacsha256",
            Issuer = TestIssuer,
            Audience = TestAudience,
            AccessTokenLifetime = TimeSpan.FromMinutes(15)
        };

        var refreshTokenService = Substitute.For<IRefreshTokenService>();
        var logger = Substitute.For<ILogger<AuthService>>();

        return new AuthService(db, Options.Create(jwtOptions), refreshTokenService, logger, clock);
    }

    [Fact]
    public void GenerateAccessToken_ForAnyUser_IncludesSubAndEmailClaims()
    {
        // Arrange
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(clock);
        var user = new User { Id = 7, Email = "trainee@thinkbridge.com" };

        // Act
        var token = sut.GenerateAccessToken(user);
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // Assert
        parsed.Claims.Should().Contain(c => c.Type == "sub" && c.Value == "7");
        parsed.Claims.Should().Contain(c => c.Type == "email" && c.Value == "trainee@thinkbridge.com");
    }

    [Fact]
    public void GenerateAccessToken_ForAnyUser_IncludesAllSixScopes()
    {
        // Arrange
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(clock);
        var user = new User { Id = 1, Email = "user@thinkbridge.com" };
        var expectedScopes = new[]
        {
            "quotes.read", "quotes.write", "quotes.delete",
            "collections.read", "collections.write", "collections.delete"
        };

        // Act
        var token = sut.GenerateAccessToken(user);
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var actualScopes = parsed.Claims.Where(c => c.Type == "scope").Select(c => c.Value);

        // Assert
        actualScopes.Should().BeEquivalentTo(expectedScopes);
    }

    [Fact]
    public void GenerateAccessToken_UsesClockForExpiry_SetsExpiryExactly15MinutesAfterNow()
    {
        // Arrange
        // Whole seconds only: JWT "exp"/"nbf" claims have one-second
        // resolution, so a FakeClock value with sub-second precision would
        // never round-trip back to an exactly equal DateTime.
        var fixedNow = new DateTimeOffset(2026, 3, 15, 9, 30, 0, TimeSpan.Zero);
        var clock = new FakeClock(fixedNow);
        var sut = CreateSut(clock);
        var user = new User { Id = 1, Email = "user@thinkbridge.com" };

        // Act
        var token = sut.GenerateAccessToken(user);
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // Assert
        parsed.ValidTo.Should().Be(fixedNow.UtcDateTime.AddMinutes(15));
        parsed.ValidFrom.Should().Be(fixedNow.UtcDateTime);
    }

    [Fact]
    public void GenerateAccessToken_UsesTheConfiguredIssuerAndAudience_NotHardcodedValues()
    {
        // This replaces a test that asserted GenerateAccessToken throws when
        // Jwt:Secret is missing. That guard no longer exists, and its
        // removal is the point: the options are validated at startup now, so
        // the app refuses to boot rather than serving traffic and failing at
        // the first login. A runtime check here would be unreachable code.
        //
        // What is worth testing instead is that these values genuinely come
        // FROM configuration. They used to be string literals repeated in
        // AuthService and again in the token validation setup, with only a
        // comment asking humans to keep them in step. Configuring
        // deliberately non-default values and asserting the minted token
        // carries them is what catches a regression back to a constant.
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var sut = CreateSut(clock, new JwtOptions
        {
            Secret = "unit-test-secret-that-is-long-enough-for-hmacsha256",
            Issuer = "https://some-other-issuer.example.com",
            Audience = "some-other-audience",
            AccessTokenLifetime = TimeSpan.FromMinutes(15)
        });
        var user = new User { Id = 1, Email = "user@thinkbridge.com" };

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(sut.GenerateAccessToken(user));

        parsed.Issuer.Should().Be("https://some-other-issuer.example.com");
        parsed.Audiences.Should().ContainSingle().Which.Should().Be("some-other-audience");
    }

    [Fact]
    public void GenerateAccessToken_ExpiresAfterTheConfiguredLifetime()
    {
        // The lifetime was a const in AuthService and, separately, a literal
        // 900 in the refresh endpoint's response telling clients when to
        // come back. Both now read one configured value; this pins the
        // minting half of that.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var sut = CreateSut(new FakeClock(now), new JwtOptions
        {
            Secret = "unit-test-secret-that-is-long-enough-for-hmacsha256",
            Issuer = TestIssuer,
            Audience = TestAudience,
            AccessTokenLifetime = TimeSpan.FromMinutes(42)
        });

        var parsed = new JwtSecurityTokenHandler()
            .ReadJwtToken(sut.GenerateAccessToken(new User { Id = 1, Email = "u@e.com" }));

        parsed.ValidTo.Should().Be(now.UtcDateTime.AddMinutes(42));
    }

    [Fact]
    public void HashPassword_ThenVerify_WithCorrectPassword_Succeeds()
    {
        // Arrange
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(clock);
        var password = "correct-horse-battery-staple";

        // Act
        var hash = sut.HashPassword(password);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
        var result = hasher.VerifyHashedPassword(new User(), hash, password);

        // Assert
        result.Should().NotBe(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
    }

    [Fact]
    public void HashPassword_ThenVerify_WithWrongPassword_Fails()
    {
        // Arrange
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(clock);
        var hash = sut.HashPassword("correct-horse-battery-staple");

        // Act
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
        var result = hasher.VerifyHashedPassword(new User(), hash, "wrong-password");

        // Assert
        result.Should().Be(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
    }
}
