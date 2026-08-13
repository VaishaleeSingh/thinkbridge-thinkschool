using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Services;

public interface IRefreshTokenService
{
    Task<string> GenerateTokenAsync(int userId, CancellationToken cancellationToken, string? familyId = null);
    Task<RefreshToken?> ValidateTokenAsync(string token, CancellationToken cancellationToken);
    Task RevokeTokenAsync(int tokenId, CancellationToken cancellationToken);
    Task<bool> DetectAndRevokeReuseAsync(string tokenHash, CancellationToken cancellationToken);
}

/// <summary>
/// Issues, validates, and revokes refresh tokens, and implements reuse
/// (theft) detection via token families.
///
/// How a "family" works: every token issued from one original login
/// shares a FamilyId. GenerateTokenAsync is called in two situations --
/// a fresh login (no familyId passed in -> a brand new family starts) and
/// a rotation at /refresh (the CURRENT token's familyId is passed in, so
/// the new token stays part of the same chain). If a token that's already
/// been replaced is ever presented again, that's treated as proof the
/// token was stolen and reused, and DetectAndRevokeReuseAsync kills every
/// token in that family at once -- not just the stolen one -- since a
/// legitimate client and an attacker can no longer be told apart at that
/// point.
/// </summary>
public sealed class RefreshTokenService : IRefreshTokenService
{
    private readonly QuotesDbContext _db;
    private readonly ILogger<RefreshTokenService> _logger;
    private readonly IClock _clock;
    private const int TokenLength = 32;
    private const int ExpiryDays = 7;

    public RefreshTokenService(QuotesDbContext db, ILogger<RefreshTokenService> logger, IClock clock)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
    }

    public async Task<string> GenerateTokenAsync(int userId, CancellationToken cancellationToken, string? familyId = null)
    {
        var tokenBytes = new byte[TokenLength];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes);

        var tokenHash = HashToken(token);

        // No familyId passed in -> this is a fresh login, start a new
        // chain. A familyId WAS passed in -> this is a rotation, keep the
        // caller's existing chain going instead of starting a new one.
        var resolvedFamilyId = familyId ?? Guid.NewGuid().ToString();

        var refreshToken = new RefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = _clock.UtcNow.UtcDateTime.AddDays(ExpiryDays),
            CreatedAt = _clock.UtcNow.UtcDateTime,
            FamilyId = resolvedFamilyId
        };

        _db.RefreshTokens.Add(refreshToken);
        await _db.SaveChangesAsync(cancellationToken);

        return token;
    }

    public async Task<RefreshToken?> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(token);

        var refreshToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        if (refreshToken is null)
            return null;

        // Token already replaced -> potential reuse attack
        if (refreshToken.ReplacedByToken is not null)
        {
            _logger.LogWarning(
                "Token reuse detected for UserId {UserId}. Token family: {FamilyId}",
                refreshToken.UserId,
                refreshToken.FamilyId);

            await DetectAndRevokeReuseAsync(tokenHash, cancellationToken);
            return null;
        }

        if (!refreshToken.IsValid)
            return null;

        return refreshToken;
    }

    public async Task RevokeTokenAsync(int tokenId, CancellationToken cancellationToken)
    {
        var token = await _db.RefreshTokens.FindAsync(new object[] { tokenId }, cancellationToken: cancellationToken);
        if (token is not null)
        {
            token.RevokedAt = _clock.UtcNow.UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> DetectAndRevokeReuseAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        if (token is null || token.FamilyId is null)
            return false;

        // Revoke all tokens in the same family
        var familyTokens = await _db.RefreshTokens
            .Where(rt => rt.FamilyId == token.FamilyId)
            .ToListAsync(cancellationToken);

        foreach (var familyToken in familyTokens)
        {
            familyToken.RevokedAt = _clock.UtcNow.UtcDateTime;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogError(
            "Security Event: Revoked entire token family {FamilyId} for UserId {UserId} due to token reuse",
            token.FamilyId,
            token.UserId);

        return true;
    }

    private static string HashToken(string token)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hash);
    }
}
