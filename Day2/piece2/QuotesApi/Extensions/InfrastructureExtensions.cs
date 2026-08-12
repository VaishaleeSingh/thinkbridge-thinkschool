using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Data;
using QuotesApi.Repositories;
using QuotesApi.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace QuotesApi.Extensions;

/// <summary>
/// This file wires up everything the app needs before it can handle a
/// single request: the database, the repositories, small helper services,
/// and — as of Day 3 — authentication.
///
/// Everything lives inside one method, AddInfrastructure(), which Program.cs
/// calls once at startup. Grouping registrations here (instead of writing
/// them directly in Program.cs) keeps Program.cs short and keeps all the
/// "how do these pieces get built" decisions in one place.
/// </summary>
public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ------------------------------------------------------------
        // STEP 1: Database
        // ------------------------------------------------------------
        // Scoped — one DbContext (and the repositories built on it) per
        // request. Sharing a DbContext across requests isn't thread-safe;
        // a shorter-than-request lifetime would just churn connections.
        services.AddDbContext<QuotesDbContext>(options =>
            options.UseSqlite(
                configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=quotes.db"));

        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();

        // ------------------------------------------------------------
        // STEP 2: Small helper services
        // ------------------------------------------------------------
        // Singleton — IClock holds no per-request state, so one instance
        // can safely serve the app's whole lifetime. This is also what
        // makes it swappable in tests: register a FakeClock singleton
        // instead and every consumer sees the fixed instant.
        services.AddSingleton<IClock, QuotesApi.Services.SystemClock>();

        // Transient — stateless, cheap to construct, nothing to share.
        // A new instance per resolution is fine because there's no
        // per-request or app-wide state to keep consistent.
        services.AddTransient<IQuoteTextNormalizer, QuoteTextNormalizer>();

        // ------------------------------------------------------------
        // STEP 3: Authentication (Day 3 — Entra ID + existing custom JWT)
        // ------------------------------------------------------------
        // WHY TWO AUTH SCHEMES?
        // Before Day 3, this API only understood tokens that our own
        // AuthService created and signed ("CustomJwt" below). Day 3 adds
        // Azure Entra ID as a second, Microsoft-managed identity provider
        // for customer-facing apps (e.g. a React/Angular SPA).
        //
        // Instead of ripping out the old system, we run BOTH schemes side
        // by side:
        //   - "CustomJwt" -> validates tokens issued by our own AuthService
        //                    (used by internal tools / CLI / old clients)
        //   - "EntraId"   -> validates tokens issued by Azure Entra ID
        //                    (used by customer-facing SPAs)
        //   - "MultiScheme" -> a router that looks at each incoming token
        //                      and decides which of the two validators
        //                      above should check it
        //
        // This means nothing that already worked breaks, and new clients
        // can start using Entra ID immediately.

        var azureAdSettings = configuration.GetSection("AzureAd");
        var customJwtSecret = configuration["Jwt:Secret"];

        if (string.IsNullOrEmpty(customJwtSecret))
        {
            throw new InvalidOperationException(
                "Jwt:Secret is missing from configuration. " +
                "Add it under appsettings.json -> \"Jwt\": { \"Secret\": \"...\" }.");
        }

        services
            .AddAuthentication(defaultScheme: "MultiScheme")

            // --- Validator #1: our own, self-issued JWTs -----------------
            // These are the tokens AuthService.GenerateAccessToken() creates.
            // We already know the exact secret, issuer, and audience used
            // to sign them, so validation here is a straightforward
            // "does the signature match, and are the claims what we expect".
            .AddJwtBearer("CustomJwt", options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(customJwtSecret)),

                    ValidateIssuer = true,
                    ValidIssuer = "https://yourapp.com",

                    ValidateAudience = true,
                    ValidAudience = "quotes-api",

                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero // don't give expired tokens extra grace time
                };
            })

            // --- Validator #2: tokens issued by Azure Entra ID -----------
            // We don't sign these tokens ourselves, so we can't hardcode a
            // secret key like above. Instead we point at Entra's "Authority"
            // (a URL under login.microsoftonline.com for our tenant), and
            // ASP.NET Core automatically fetches Microsoft's public signing
            // keys from there to verify the token's signature.
            .AddJwtBearer("EntraId", options =>
            {
                options.Authority = azureAdSettings["Authority"];
                options.Audience = azureAdSettings["Audience"];

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            })

            // --- The router: decide which validator should run -----------
            // Every incoming request only carries one Authorization header.
            // ASP.NET Core needs to know up front which of the two schemes
            // above should attempt to validate it. AddPolicyScheme lets us
            // write that decision ourselves instead of picking one scheme
            // as the permanent default.
            //
            // The trick: peek at the token's "aud" (audience) claim WITHOUT
            // validating it yet. Entra ID always issues audiences shaped
            // like "api://something", while our own tokens use a plain
            // string ("quotes-api"). That one difference is enough to route
            // correctly.
            .AddPolicyScheme("MultiScheme", "Custom JWT or Entra JWT", options =>
            {
                options.ForwardDefaultSelector = httpContext =>
                {
                    var authorizationHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();

                    // No token at all -> nothing to inspect. Forward to
                    // CustomJwt anyway; it will correctly reject the request
                    // with 401 since there's no token to validate.
                    if (string.IsNullOrEmpty(authorizationHeader))
                        return "CustomJwt";

                    try
                    {
                        var rawToken = authorizationHeader.Replace("Bearer ", "").Trim();
                        var tokenReader = new JwtSecurityTokenHandler();

                        // CanReadToken/ReadToken only parse the token's
                        // structure — they do NOT check the signature.
                        // Real validation still happens afterward, inside
                        // whichever scheme we forward to.
                        if (tokenReader.CanReadToken(rawToken))
                        {
                            var parsedToken = tokenReader.ReadToken(rawToken) as JwtSecurityToken;
                            var audience = parsedToken?.Claims
                                .FirstOrDefault(claim => claim.Type == "aud")?.Value ?? "";

                            if (audience.Contains("api://"))
                                return "EntraId";
                        }
                    }
                    catch
                    {
                        // Token is malformed / not a JWT at all. Fall through
                        // to CustomJwt below, which will reject it with 401
                        // through the normal validation pipeline.
                    }

                    return "CustomJwt";
                };
            });

        // Authorization is the step that actually enforces "you must be
        // authenticated" on endpoints marked with .RequireAuthorization().
        // Adding authentication above without this would validate tokens
        // when present but never require one in the first place.
        services.AddAuthorization();

        return services;
    }
}
