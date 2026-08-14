namespace QuotesApi.Models;

/// <summary>
/// A long-lived token that lets a client get a new access token without
/// logging in again. Access tokens (the JWTs AuthService issues) are kept
/// short-lived on purpose (15 minutes) so a stolen one stops working
/// quickly; refresh tokens are what let a legitimate client keep its
/// session going past that without re-entering a password every 15
/// minutes.
///
/// Only the HASH of the actual token is stored here (see TokenHash) --
/// same reasoning as PasswordHash on User: if this table ever leaked, the
/// raw tokens themselves still couldn't be read out of it.
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    // Set the moment this token gets exchanged for a new one at /refresh.
    // A non-null value here means "this token has already been used" --
    // which is exactly what lets ValidateTokenAsync detect reuse.
    public string? ReplacedByToken { get; set; }

    public DateTime CreatedAt { get; set; }

    // Every token issued from the same original login shares one
    // FamilyId, and each rotation carries that same FamilyId forward (see
    // RefreshTokenService.GenerateTokenAsync). If a token that was already
    // replaced gets presented again -- a stolen, already-used token being
    // reused -- the WHOLE family is revoked, not just that one token,
    // because at that point a legitimate client and an attacker can no
    // longer be told apart.
    public string? FamilyId { get; set; }

    public User User { get; set; } = null!;

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsValid => !IsExpired && !IsRevoked && ReplacedByToken is null;
}
