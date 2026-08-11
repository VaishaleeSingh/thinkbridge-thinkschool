using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Data;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Scoped — one DbContext (and the repositories built on it) per
        // request. Sharing a DbContext across requests isn't thread-safe;
        // a shorter-than-request lifetime would just churn connections.
        services.AddDbContext<QuotesDbContext>(options =>
            options.UseSqlite(
                configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=quotes.db"));

        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();

        // Singleton — IClock holds no per-request state, so one instance
        // can safely serve the app's whole lifetime. This is also what
        // makes it swappable in tests: register a FakeClock singleton
        // instead and every consumer sees the fixed instant.
        services.AddSingleton<IClock, SystemClock>();

        // Transient — stateless, cheap to construct, nothing to share.
        // A new instance per resolution is fine because there's no
        // per-request or app-wide state to keep consistent.
        services.AddTransient<IQuoteTextNormalizer, QuoteTextNormalizer>();

        // Scoped — auth service needs DbContext for user lookup
        services.AddScoped<IAuthService, AuthService>();

        // JWT authentication
        var jwtKey = configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key not configured");
        var jwtIssuer = configuration["Jwt:Issuer"] ?? "QuotesApi";
        var jwtAudience = configuration["Jwt:Audience"] ?? "QuotesApi";

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtKey)),
                    ValidateIssuer = true,
                    ValidIssuer = jwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = jwtAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorization();

        return services;
    }
}