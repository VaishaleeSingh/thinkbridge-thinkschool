using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;
using QuotesApi.Data;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

/// <summary>
/// /api/auth/login, /refresh, and /logout -- the three endpoints that hand
/// out and manage our own self-issued JWTs (the "CustomJwt" scheme).
///
/// These endpoints are deliberately NOT behind .RequireAuthorization():
/// you can't be asked to prove who you are with a token in order to get
/// your very first token. (Entra ID users skip this file entirely --
/// Microsoft authenticates them directly and hands back its own token.)
/// </summary>
public static class AuthEndpointExtensions
{
    public static IEndpointRouteBuilder MapAuthEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        // POST /api/auth/login -- trade an email + password for an access
        // token (short-lived, 15 min) and a refresh token (long-lived, 7
        // days).
        group.MapPost("/login", async (
            LoginRequest request,
            IAuthService authService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["credentials"] = new[] { "Email and password are required." }
                });
            }

            var result = await authService.LoginAsync(
                request.Email,
                request.Password,
                cancellationToken);

            if (result is null)
                return Results.Unauthorized();

            var (accessToken, refreshToken, expiresIn) = result.Value;

            return Results.Ok(new LoginResponse(
                accessToken,
                refreshToken,
                expiresIn,
                "Bearer"));
        });

        // POST /api/auth/refresh -- trade a still-valid refresh token for a
        // brand new access token + refresh token pair. The old refresh
        // token is immediately marked as replaced (see
        // RefreshTokenService.ValidateTokenAsync) so it can never be used
        // again -- presenting it a second time is treated as theft, not a
        // retry, and revokes the whole token family (see
        // RefreshTokenService.DetectAndRevokeReuseAsync).
        //
        // QuotesDbContext is injected directly as a Minimal API parameter
        // here (ASP.NET Core resolves it from the request's own DI scope)
        // instead of manually building a second scope with
        // app.ServiceProvider.CreateScope() the way Day 1's version did --
        // that manual approach created a disconnected second DbContext
        // just to save a parameter, which is unnecessary and easy to get
        // subtly wrong.
        group.MapPost("/refresh", async (
            RefreshRequest request,
            IRefreshTokenService refreshTokenService,
            IAuthService authService,
            QuotesDbContext db,
            IOptions<JwtOptions> jwtOptions,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["refreshToken"] = new[] { "Refresh token is required." }
                });

            var storedToken = await refreshTokenService.ValidateTokenAsync(
                request.RefreshToken,
                cancellationToken);

            if (storedToken is null)
                return Results.Unauthorized();

            var user = await db.Users.FindAsync(
                new object[] { storedToken.UserId },
                cancellationToken);

            if (user is null)
                return Results.Unauthorized();

            // Generate the new pair. The new refresh token carries forward
            // the SAME FamilyId as the one being replaced, so the chain
            // stays intact -- this is what lets a reuse of the OLD token
            // later revoke this new one too, instead of the two being
            // unrelated.
            var newAccessToken = authService.GenerateAccessToken(user);
            var newRefreshToken = await refreshTokenService.GenerateTokenAsync(
                user.Id,
                cancellationToken,
                storedToken.FamilyId);

            // Mark the old token as replaced so it can't be used again.
            storedToken.ReplacedByToken = newRefreshToken;
            await db.SaveChangesAsync(cancellationToken);

            // Derived from the SAME configured lifetime the token was
            // actually minted with, rather than a hand-copied constant. It
            // was previously the literal 900 with a comment asking whoever
            // changed AuthService to remember to change this too. Nothing
            // enforced that; change one and the API keeps issuing valid
            // tokens while telling clients the wrong expiry, so they
            // refresh too late (users see spurious 401s) or too early
            // (needless load). No test would have caught it.
            var expiresIn = (int)jwtOptions.Value.AccessTokenLifetime.TotalSeconds;

            return Results.Ok(new LoginResponse(
                newAccessToken,
                newRefreshToken,
                expiresIn,
                "Bearer"));
        });

        // POST /api/auth/logout -- revoke a refresh token early (e.g. the
        // user clicked "sign out"), so it can't be used to get new access
        // tokens even though it hasn't expired yet.
        group.MapPost("/logout", async (
            LogoutRequest request,
            IRefreshTokenService refreshTokenService,
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["refreshToken"] = new[] { "Refresh token is required." }
                });

            var token = await db.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == HashToken(request.RefreshToken), cancellationToken);

            if (token is not null)
            {
                await refreshTokenService.RevokeTokenAsync(token.Id, cancellationToken);
            }

            return Results.NoContent();
        });

        return app;
    }

    private static string HashToken(string token)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hash);
    }
}

public record RefreshRequest(string RefreshToken);

public record LogoutRequest(string RefreshToken);

public record LoginRequest(string Email, string Password);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    string TokenType);
