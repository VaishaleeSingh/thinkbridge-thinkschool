using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

public class RefreshTokenServiceTests
{
    private static QuotesDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new QuotesDbContext(options);
    }

    private static RefreshTokenService CreateSut(QuotesDbContext db, IClock clock)
    {
        var logger = Substitute.For<ILogger<RefreshTokenService>>();
        return new RefreshTokenService(db, logger, clock);
    }

    [Fact]
    public async Task GenerateTokenAsync_WithNoFamilyIdProvided_StartsANewFamily()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);

        // Act
        var token = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);

        // Assert
        var stored = await db.RefreshTokens.SingleAsync();
        stored.FamilyId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GenerateTokenAsync_WithFamilyIdProvided_KeepsTheSameFamily()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        var existingFamilyId = Guid.NewGuid().ToString();

        // Act
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None, existingFamilyId);

        // Assert
        var stored = await db.RefreshTokens.SingleAsync();
        stored.FamilyId.Should().Be(existingFamilyId);
    }

    [Fact]
    public async Task GenerateTokenAsync_SetsExpiresAtToClockPlus7Days()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var fixedNow = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(fixedNow);
        var sut = CreateSut(db, clock);

        // Act
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);

        // Assert
        var stored = await db.RefreshTokens.SingleAsync();
        stored.ExpiresAt.Should().Be(fixedNow.UtcDateTime.AddDays(7));
    }

    [Fact]
    public async Task GenerateTokenAsync_SetsCreatedAtToClockNow()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var fixedNow = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(fixedNow);
        var sut = CreateSut(db, clock);

        // Act
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);

        // Assert
        var stored = await db.RefreshTokens.SingleAsync();
        stored.CreatedAt.Should().Be(fixedNow.UtcDateTime);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithUnknownToken_ReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);

        // Act
        var result = await sut.ValidateTokenAsync("a-token-that-was-never-issued", CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_WithFreshToken_ReturnsIt()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        var token = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);

        // Act
        var result = await sut.ValidateTokenAsync(token, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(1);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithExpiredToken_ReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        var token = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);

        // IsExpired compares against the real system clock, not IClock,
        // so the row is edited directly to simulate time having passed.
        var stored = await db.RefreshTokens.SingleAsync();
        stored.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        // Act
        var result = await sut.ValidateTokenAsync(token, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_WithRevokedToken_ReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        var token = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);
        var stored = await db.RefreshTokens.SingleAsync();
        stored.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Act
        var result = await sut.ValidateTokenAsync(token, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_WithAlreadyReplacedToken_ReturnsNull()
    {
        // Arrange: simulate rotation having already happened once, then
        // the OLD token is presented again -- exactly the reuse scenario.
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        var originalToken = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);
        var stored = await db.RefreshTokens.SingleAsync();
        stored.ReplacedByToken = "some-newer-token-hash";
        await db.SaveChangesAsync();

        // Act
        var result = await sut.ValidateTokenAsync(originalToken, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_WithAlreadyReplacedToken_RevokesEntireFamily()
    {
        // Arrange: two tokens rotated within the same family, then the
        // FIRST (already-replaced) one is presented again.
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        var firstToken = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);
        var firstStored = await db.RefreshTokens.SingleAsync();
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None, firstStored.FamilyId);
        firstStored.ReplacedByToken = "the-second-token-hash";
        await db.SaveChangesAsync();

        // Act
        await sut.ValidateTokenAsync(firstToken, CancellationToken.None);

        // Assert
        var allTokensInFamily = await db.RefreshTokens
            .Where(t => t.FamilyId == firstStored.FamilyId)
            .ToListAsync();
        allTokensInFamily.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull());
    }

    [Fact]
    public async Task ValidateTokenAsync_WithAlreadyReplacedToken_LeavesUnrelatedFamilyUntouched()
    {
        // Arrange: an unrelated, legitimate token family belonging to a
        // different login must not be caught up in another family's
        // reuse-detection sweep.
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);

        var compromisedToken = await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);
        var compromisedStored = await db.RefreshTokens.SingleAsync(t => t.UserId == 1);
        compromisedStored.ReplacedByToken = "a-newer-token-hash";
        await db.SaveChangesAsync();

        await sut.GenerateTokenAsync(userId: 2, CancellationToken.None);
        var unrelatedStored = await db.RefreshTokens.SingleAsync(t => t.UserId == 2);

        // Act
        await sut.ValidateTokenAsync(compromisedToken, CancellationToken.None);

        // Assert
        var unrelatedAfter = await db.RefreshTokens.SingleAsync(t => t.Id == unrelatedStored.Id);
        unrelatedAfter.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task DetectAndRevokeReuseAsync_WithUnknownHash_ReturnsFalse()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);

        // Act
        var result = await sut.DetectAndRevokeReuseAsync("a-hash-that-does-not-exist", CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAndRevokeReuseAsync_WithKnownFamilyMember_RevokesWholeFamilyAndReturnsTrue()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);
        var firstStored = await db.RefreshTokens.SingleAsync();
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None, firstStored.FamilyId);

        // Act
        var result = await sut.DetectAndRevokeReuseAsync(firstStored.TokenHash, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var allTokensInFamily = await db.RefreshTokens
            .Where(t => t.FamilyId == firstStored.FamilyId)
            .ToListAsync();
        allTokensInFamily.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull());
    }

    [Fact]
    public async Task RevokeTokenAsync_SetsRevokedAtToClockNow()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var fixedNow = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(fixedNow);
        var sut = CreateSut(db, clock);
        await sut.GenerateTokenAsync(userId: 1, CancellationToken.None);
        var stored = await db.RefreshTokens.SingleAsync();

        // Act
        await sut.RevokeTokenAsync(stored.Id, CancellationToken.None);

        // Assert
        var afterRevoke = await db.RefreshTokens.SingleAsync(t => t.Id == stored.Id);
        afterRevoke.RevokedAt.Should().Be(fixedNow.UtcDateTime);
    }

    [Fact]
    public async Task RevokeTokenAsync_WithUnknownId_DoesNothing()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sut = CreateSut(db, clock);

        // Act
        var act = async () => await sut.RevokeTokenAsync(999, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }
}
