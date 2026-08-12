using Microsoft.EntityFrameworkCore;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class AuthEndpointExtensions
{
    public static IEndpointRouteBuilder MapAuthEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

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

        group.MapPost("/refresh", async (
            RefreshRequest request,
            IRefreshTokenService refreshTokenService,
            IAuthService authService,
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

            var user = await app.ServiceProvider
                .CreateScope()
                .ServiceProvider
                .GetRequiredService<QuotesApi.Data.QuotesDbContext>()
                .Users
                .FindAsync(new object[] { storedToken.UserId }, cancellationToken);

            if (user is null)
                return Results.Unauthorized();

            // Generate new tokens
            var newAccessToken = authService.GenerateAccessToken(user);
            var newRefreshToken = await refreshTokenService.GenerateTokenAsync(user.Id, cancellationToken);

            // Mark old token as replaced
            storedToken.ReplacedByToken = newRefreshToken;
            await app.ServiceProvider
                .CreateScope()
                .ServiceProvider
                .GetRequiredService<QuotesApi.Data.QuotesDbContext>()
                .SaveChangesAsync(cancellationToken);

            const int expiresIn = 900; // 15 minutes

            return Results.Ok(new LoginResponse(
                newAccessToken,
                newRefreshToken,
                expiresIn,
                "Bearer"));
        });

        group.MapPost("/logout", async (
            LogoutRequest request,
            IRefreshTokenService refreshTokenService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["refreshToken"] = new[] { "Refresh token is required." }
                });

            var token = await app.ServiceProvider
                .CreateScope()
                .ServiceProvider
                .GetRequiredService<QuotesApi.Data.QuotesDbContext>()
                .RefreshTokens
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
