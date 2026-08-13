using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Observability;

namespace QuotesApi.Services;

public interface IAuthService
{
    Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken);

    string HashPassword(string password);
    string GenerateAccessToken(User user);
}

/// <summary>
/// Issues our own self-issued JWTs (the "CustomJwt" scheme) for clients
/// that log in with an email + password -- as opposed to Entra ID
/// clients, who authenticate with Microsoft directly and never touch this
/// class at all.
///
/// IMPORTANT: the issuer, audience, and secret used in GenerateAccessToken
/// below are not arbitrary -- they have to exactly match the "CustomJwt"
/// validation rules configured in InfrastructureExtensions.cs, otherwise
/// every token this class issues would be rejected by the API's own auth
/// middleware the moment it came back in on a request.
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly QuotesDbContext _db;
    private readonly IConfiguration _config;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ILogger<AuthService> _logger;

    // Asked for through this interface instead of calling DateTime.UtcNow
    // directly, so that "when was this token issued / when does it
    // expire" is something a test can pin to an exact instant rather than
    // asserting something fuzzy like "roughly 15 minutes from whenever the
    // test happened to run." Production gets the real SystemClock via DI;
    // tests substitute a FakeClock.
    private readonly IClock _clock;

    // 15 minutes. Kept short on purpose: if an access token is ever
    // stolen, it stops working quickly on its own. Long-lived sessions are
    // handled by refresh tokens instead (7 days, see RefreshTokenService),
    // which can be individually revoked and are rotated on every use.
    private const int AccessTokenLifetimeMinutes = 15;

    // This project has no roles/admin table (see MustOwnQuoteHandler for
    // why): every logged-in user gets the same full set of scopes here.
    // What actually stops one user from touching another user's data is
    // the resource-level ownership check on delete, not scope.
    private static readonly string[] AllScopes =
    {
        "quotes.read", "quotes.write", "quotes.delete",
        "collections.read", "collections.write", "collections.delete"
    };

    public AuthService(
        QuotesDbContext db,
        IConfiguration config,
        IRefreshTokenService refreshTokenService,
        ILogger<AuthService> logger,
        IClock clock)
    {
        _db = db;
        _config = config;
        _refreshTokenService = refreshTokenService;
        _logger = logger;
        _clock = clock;
    }

    public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            _logger.LogWarning("Login failed for email {Email}: user not found", email);
            return null;
        }

        // Password verification gets its own span. PasswordHasher runs
        // PBKDF2 with ~100k iterations -- deliberately expensive, and by
        // design usually the slowest part of a login. It is also pure CPU
        // work, so neither the EF Core nor the HttpClient instrumentation
        // sees it: without this span a slow login trace would show two
        // quick database queries either side of a large unexplained gap.
        //
        // Scoped to a block rather than "using var" for the whole method,
        // so the span measures the hashing and nothing after it.
        //
        // Tagged with the numeric user id, NOT the email: trace backends
        // are somewhere personal data accumulates quietly, and an id is
        // enough to correlate with the rest of the system.
        PasswordVerificationResult result;
        using (var activity = QuotesActivitySource.Instance.StartActivity("verify-password"))
        {
            activity?.SetTag("user.id", user.Id);

            var hasher = new PasswordHasher<User>();
            result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        }

        if (result == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Login failed for email {Email}: invalid password", email);
            return null;
        }

        var accessToken = GenerateAccessToken(user);

        // A fresh login always starts a brand new token family (no
        // familyId passed in) -- see RefreshTokenService.GenerateTokenAsync.
        var refreshToken = await _refreshTokenService.GenerateTokenAsync(user.Id, cancellationToken);
        const int expiresIn = AccessTokenLifetimeMinutes * 60;

        _logger.LogInformation("User {Email} logged in successfully", email);

        return (accessToken, refreshToken, expiresIn);
    }

    public string HashPassword(string password)
    {
        var hasher = new PasswordHasher<User>();
        return hasher.HashPassword(new User(), password);
    }

    public string GenerateAccessToken(User user)
    {
        var secret = _config["Jwt:Secret"]
            ?? throw new InvalidOperationException(
                "Jwt:Secret is missing from configuration. " +
                "Add it under appsettings.json -> \"Jwt\": { \"Secret\": \"...\" }.");

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        // "sub" (the short, raw claim name) is what the rest of the app --
        // MustOwnQuoteHandler, the quote-creation endpoint, the collection
        // endpoints -- looks for as the caller's id, so it's issued here
        // in that exact shape rather than the longer ClaimTypes.NameIdentifier
        // form.
        var claims = new List<Claim>
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("email", user.Email)
        };

        foreach (var scope in AllScopes)
            claims.Add(new Claim("scope", scope));

        var now = _clock.UtcNow.UtcDateTime;

        var token = new JwtSecurityToken(
            issuer: "https://yourapp.com",
            audience: "quotes-api",
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(AccessTokenLifetimeMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
