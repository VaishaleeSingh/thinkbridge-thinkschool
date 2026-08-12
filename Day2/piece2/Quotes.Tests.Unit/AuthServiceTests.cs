using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private static AuthService CreateSut(
        IClock clock,
        string? jwtSecret = "unit-test-secret-that-is-long-enough-for-hmacsha256")
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new QuotesDbContext(options);

        var configValues = new Dictionary<string, string?>();
        if (jwtSecret is not null)
            configValues["Jwt:Secret"] = jwtSecret;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var refreshTokenService = Substitute.For<IRefreshTokenService>();
        var logger = Substitute.For<ILogger<AuthService>>();

        return new AuthService(db, config, refreshTokenService, logger, clock);
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
    public void GenerateAccessToken_WhenJwtSecretIsMissing_ThrowsInvalidOperationException()
    {
        // Arrange
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(clock, jwtSecret: null);
        var user = new User { Id = 1, Email = "user@thinkbridge.com" };

        // Act
        var act = () => sut.GenerateAccessToken(user);

        // Assert
        act.Should().Throw<InvalidOperationException>();
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
