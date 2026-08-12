using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;
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

        // ------------------------------------------------------------
        // STEP 4: Authorization policies and claims (Day 3, part 2)
        // ------------------------------------------------------------
        // Authentication (above) only answers "who is this." Everything
        // below answers the separate question "are they allowed to do
        // this." Two different mechanisms are used, because two different
        // kinds of rule are needed:
        //
        //   - can-read-quotes / can-edit-quotes / can-delete-quotes are
        //     CLAIM-based policies: they can be decided purely from the
        //     caller's token, before any endpoint code runs at all. See
        //     .RequireAuthorization("can-edit-quotes") etc. in
        //     QuoteEndpointExtensions.cs.
        //
        //   - "Can this caller delete THIS SPECIFIC quote" cannot be
        //     answered from the token alone — it depends on who created
        //     that particular row in the database. That's a RESOURCE-based
        //     rule (MustOwnQuoteRequirement/MustOwnQuoteHandler), checked
        //     imperatively inside the DELETE endpoint after the quote has
        //     been loaded, not declared here as a policy.
        //
        // This project deliberately has no roles/admin table. Every
        // authenticated caller gets the same three scopes (see where
        // tokens are issued/tested); the ownership check is what actually
        // stops one user from deleting another user's quote, not scope.
        services.AddAuthorization(options =>
        {
            options.AddPolicy("can-read-quotes", policy =>
                policy.RequireClaim("scope", "quotes.read"));

            options.AddPolicy("can-edit-quotes", policy =>
                policy.RequireClaim("scope", "quotes.write"));

            options.AddPolicy("can-delete-quotes", policy =>
                policy.RequireClaim("scope", "quotes.delete"));
        });

        // Normalizes Entra ID's "scp"/"roles" claims into the same "scope"
        // claim shape our own tokens use, so the policies above work
        // identically no matter which scheme authenticated the caller.
        // See ScopeClaimsTransformation for why this is necessary.
        services.AddTransient<IClaimsTransformation, ScopeClaimsTransformation>();

        // Registers the resource-based ownership rule so it can be
        // resolved via IAuthorizationService.AuthorizeAsync(...) inside
        // the DELETE endpoint.
        services.AddSingleton<IAuthorizationHandler, MustOwnQuoteHandler>();

        return services;
    }
}
