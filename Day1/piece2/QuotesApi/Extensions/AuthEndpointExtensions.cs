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

        return app;
    }
}

public record LoginRequest(string Email, string Password);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    string TokenType);
