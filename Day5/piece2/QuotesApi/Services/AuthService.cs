using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;
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

    // IOptions, NOT IOptionsSnapshot, and that choice is deliberate.
    //
    // IOptionsSnapshot re-reads configuration per request, which sounds
    // strictly better. It would be actively harmful here. The other half of
    // this feature -- the TokenValidationParameters in
    // InfrastructureExtensions that decide whether a token is acceptable --
    // is baked into the authentication handler once at startup and cannot
    // be re-read. If this side picked up a changed issuer, audience or
    // signing key while the validating side kept the old one, the API would
    // mint tokens it then rejects itself: every caller gets 401, and
    // nothing anywhere logs an error explaining why. Fixing the signing
    // side alone to re-read config would create precisely the drift this
    // options type was introduced to eliminate.
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ILogger<AuthService> _logger;

    // Asked for through this interface instead of calling DateTime.UtcNow
    // directly, so that "when was this token issued / when does it
    // expire" is something a test can pin to an exact instant rather than
    // asserting something fuzzy like "roughly 15 minutes from whenever the
    // test happened to run." Production gets the real SystemClock via DI;
    // tests substitute a FakeClock.
    private readonly IClock _clock;

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
        IOptions<JwtOptions> jwtOptions,
        IRefreshTokenService refreshTokenService,
        ILogger<AuthService> logger,
        IClock clock)
    {
        _db = db;
        _jwtOptions = jwtOptions;
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
            // Deliberately logs NO identifier. Until Day 4's App Insights
            // piece these lines went to a console that scrolled away;
            // they now go to a cloud store with weeks of retention,
            // readable by anyone with reader access on the resource. An
            // email address typed into a login box is personal data
            // whether or not an account exists for it -- and for a failed
            // lookup there is no account to attribute it to anyway. The
            // TraceId on this line still ties it to the exact request, so
            // nothing is lost for debugging.
            _logger.LogWarning("Login failed: no account exists for the supplied email address");
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
            // The user id rather than the email: enough to correlate with
            // everything else in the system, without putting an address
            // into telemetry.
            _logger.LogWarning("Login failed for user {UserId}: invalid password", user.Id);
            return null;
        }

        var accessToken = GenerateAccessToken(user);

        // A fresh login always starts a brand new token family (no
        // familyId passed in) -- see RefreshTokenService.GenerateTokenAsync.
        var refreshToken = await _refreshTokenService.GenerateTokenAsync(user.Id, cancellationToken);
        var expiresIn = (int)_jwtOptions.Value.AccessTokenLifetime.TotalSeconds;

        _logger.LogInformation("User {UserId} logged in successfully", user.Id);

        return (accessToken, refreshToken, expiresIn);
    }

    public string HashPassword(string password)
    {
        var hasher = new PasswordHasher<User>();
        return hasher.HashPassword(new User(), password);
    }

    public string GenerateAccessToken(User user)
    {
        // No null-check or "missing config" throw here any more: the
        // options are validated at startup (see AddInfrastructure), so by
        // the time any request reaches this method the values are known
        // good. A guard here would be unreachable code pretending to be
        // safety.
        var jwt = _jwtOptions.Value;

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret));
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
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(jwt.AccessTokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
