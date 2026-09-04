using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;
using QuotesApi.BackgroundJobs;
using QuotesApi.Configuration;
using QuotesApi.Data;
using QuotesApi.Queries;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

/// <summary>
/// This file wires up everything the app needs before it can handle a
/// single request: the database, the repositories, small helper services,
/// authentication, and authorization.
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
        // Scoped -- one DbContext (and the repositories built on it) per
        // request. Sharing a DbContext across requests isn't thread-safe;
        // a shorter-than-request lifetime would just churn connections.
        var defaultConnection =
            configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=quotes.db";

        var configuredProvider = configuration["Database:Provider"];
        var useSqlServer = string.Equals(configuredProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            || defaultConnection.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || defaultConnection.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase);

        // Day 21 -- the (sp, options) overload, so the DB-command counter can
        // be attached. An interceptor that is silently absent reports zero
        // commands, which is indistinguishable from a perfect cache: the one
        // wrong answer this measurement must not be able to give.
        services.AddDbContext<QuotesDbContext>((serviceProvider, options) =>
        {
            options.AddInterceptors(
                serviceProvider.GetRequiredService<Observability.DbCommandCounterInterceptor>());

            if (useSqlServer)
            {
                options.UseSqlServer(defaultConnection);
                return;
            }

            options.UseSqlite(defaultConnection);
        });

        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();

        // Day 12 -- the query side, registered alongside the repository rather
        // than replacing it. Scoped for the same reason: it takes the scoped
        // QuotesDbContext, so it must not outlive the request.
        services.AddScoped<ICollectionQueries, CollectionQueries>();

        // ------------------------------------------------------------
        // STEP 2: Small helper services
        // ------------------------------------------------------------
        // Singleton -- IClock holds no per-request state, so one instance
        // can safely serve the app's whole lifetime. This is also what
        // makes it swappable in tests: register a FakeClock singleton
        // instead and every consumer sees the fixed instant.
        services.AddSingleton<IClock, QuotesApi.Services.SystemClock>();

        // Transient -- stateless, cheap to construct, nothing to share.
        // A new instance per resolution is fine because there's no
        // per-request or app-wide state to keep consistent.
        services.AddTransient<IQuoteTextNormalizer, QuoteTextNormalizer>();

        // Day 18 -- a bounded in-memory queue shared by HTTP producers and
        // one hosted consumer. The processor remains scoped because it owns a
        // scoped QuotesDbContext; QueuedBackgroundJobService creates a fresh
        // scope for every item rather than capturing a request scope.
        services
            .AddOptions<BackgroundJobQueueOptions>()
            .Bind(configuration.GetSection(BackgroundJobQueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IBackgroundJobQueue, InMemoryBackgroundJobQueue>();
        services.AddSingleton<IBackgroundJobStore, InMemoryBackgroundJobStore>();
        services.AddScoped<IQuoteAuthorReportProcessor, QuoteAuthorReportProcessor>();
        services.AddHostedService<QueuedBackgroundJobService>();

        var shutdownTimeoutSeconds = configuration.GetValue<int?>(
            $"{BackgroundJobQueueOptions.SectionName}:ShutdownTimeoutSeconds") ?? 15;

        services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeoutSeconds));

        // Scoped -- both talk to QuotesDbContext (itself scoped), so they
        // need to live no longer than one request too, otherwise they'd
        // end up holding onto a DbContext from a previous, already
        // disposed request.
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // ------------------------------------------------------------
        // STEP 3: Authentication (Day 3 -- Entra ID + existing custom JWT)
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

        // Bind the typed options and validate them AT STARTUP.
        //
        // This replaces a hand-written "if the secret is missing, throw"
        // guard. Two things are better for it. Validation now covers every
        // rule (present, long enough for HMAC-SHA256, issuer and audience
        // set, lifetime in a sane range) and reports ALL failures together
        // rather than surfacing the next one only after you have fixed the
        // last. And ValidateOnStart means the app refuses to boot rather
        // than starting happily and failing at the first login attempt --
        // a misconfigured deployment should fail where a rollout notices,
        // not where a user does.
        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<PaginationOptions>()
            .Bind(configuration.GetSection(PaginationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.Configure<AzureAdOptions>(
            configuration.GetSection(AzureAdOptions.SectionName));

        // Reading the values directly here as well: the authentication
        // handlers below are configured once, during startup, and the
        // options container is not built yet at this point.
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var azureAd = configuration.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>() ?? new AzureAdOptions();

        services
            .AddAuthentication(defaultScheme: "MultiScheme")

            // --- Validator #1: our own, self-issued JWTs -----------------
            // These are the tokens AuthService.GenerateAccessToken() creates.
            // We already know the exact secret, issuer, and audience used
            // to sign them, so validation here is a straightforward
            // "does the signature match, and are the claims what we expect".
            .AddJwtBearer("CustomJwt", options =>
            {
                // Built by ForwardedUserAuthentication rather than written
                // out here, because the proxy path validates the very same
                // kind of token and must hold it to the very same standard.
                // Two copies is how one of them ends up with ValidateLifetime
                // quietly relaxed. Same key, issuer, audience, and the same
                // zero clock skew -- no extra grace for expired tokens.
                options.TokenValidationParameters =
                    ForwardedUserAuthentication.CustomJwtParameters(jwt);
            })

            // --- Validator #2: tokens issued by Azure Entra ID -----------
            // We don't sign these tokens ourselves, so we can't hardcode a
            // secret key like above. Instead we point at Entra's "Authority"
            // (a URL under login.microsoftonline.com for our tenant), and
            // ASP.NET Core automatically fetches Microsoft's public signing
            // keys from there to verify the token's signature.
            .AddJwtBearer("EntraId", options =>
            {
                options.Authority = azureAd.Authority;
                options.Audience = azureAd.Audience;

                // The Backchannel this handler uses to fetch Entra's
                // metadata and signing keys is assigned below, after the
                // authentication builder, because it needs
                // IHttpClientFactory and this delegate has no service
                // provider to resolve it from.

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                // The Day 17 BFF authenticates as an application with a
                // managed-identity token and forwards the real user in
                // X-Forwarded-Authorization. Without this, that call
                // authenticates as the *application* and then fails
                // authorization on every endpoint, because an app-only token
                // has no "sub" and carries "roles" where the policies expect
                // "scope". The gates that make honouring the header safe are
                // in ForwardedUserAuthentication -- read them before
                // changing anything here.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                        ForwardedUserAuthentication.OnTokenValidatedAsync(context, jwt, azureAd),
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
                // The actual decision logic lives in AuthSchemeSelector, as
                // a standalone, unit-testable pure function of the header
                // value -- see that class for why it isn't written inline
                // here anymore.
                options.ForwardDefaultSelector = httpContext =>
                    AuthSchemeSelector.Select(httpContext.Request.Headers["Authorization"].FirstOrDefault());
            });

        // --- Resilience for the one outbound HTTP call this API makes ---
        //
        // The "EntraId" scheme above talks to login.microsoftonline.com to
        // fetch the OIDC metadata document and signing keys it validates
        // tokens against. Left alone, ASP.NET Core builds a bare HttpClient
        // for that: no retry, no circuit breaker, one 60 second timeout.
        // A momentary blip there turns into failed authentication for every
        // Entra-issued token in flight.
        //
        // AddResilientHttpClients registers a named client wrapped in a
        // Polly pipeline (see ResilienceExtensions for the strategy order
        // and why each number is what it is); this Configure call is what
        // actually hands that client to the handler. It is written as a
        // post-configure step rather than inline in AddJwtBearer above
        // because IHttpClientFactory has to be resolved from the container,
        // and the AddJwtBearer delegate has no access to it.
        //
        // Named options, not the unnamed default: JwtBearerOptions is
        // registered per scheme, and configuring the unnamed instance would
        // silently do nothing to the "EntraId" scheme.
        // Day 22: the configuration is passed now, because every policy
        // parameter is bound from the "Resilience" section instead of being an
        // inline constant. That is what makes the circuit breaker testable in
        // under a second -- and therefore provable -- rather than a ten second
        // sleep nobody was willing to put in CI.
        services.AddResilientHttpClients(configuration);

        services
            .AddOptions<JwtBearerOptions>("EntraId")
            .Configure<IHttpClientFactory>((options, httpClientFactory) =>
            {
                options.Backchannel = httpClientFactory.CreateClient(
                    ResilienceExtensions.EntraIdClientName);
            });

        // ------------------------------------------------------------
        // STEP 4: Authorization policies and claims
        // ------------------------------------------------------------
        // Authentication (above) only answers "who is this." Everything
        // below answers the separate question "are they allowed to do
        // this." Two different mechanisms are used, because two different
        // kinds of rule are needed:
        //
        //   - can-read-quotes / can-edit-quotes / can-delete-quotes and
        //     can-read-collections / can-edit-collections /
        //     can-delete-collections are CLAIM-based policies: they can be
        //     decided purely from the caller's token, before any endpoint
        //     code runs at all. See .RequireAuthorization("...") in
        //     QuoteEndpointExtensions.cs and CollectionEndpointExtensions.cs.
        //
        //   - "Can this caller delete THIS SPECIFIC quote" cannot be
        //     answered from the token alone -- it depends on who created
        //     that particular row in the database. That's a RESOURCE-based
        //     rule (MustOwnQuoteRequirement/MustOwnQuoteHandler), checked
        //     imperatively inside the DELETE endpoint after the quote has
        //     been loaded, not declared here as a policy.
        //
        // This project deliberately has no roles/admin table. Every
        // authenticated caller gets the same six scopes (see where tokens
        // are issued/tested); the ownership check on quotes is what
        // actually stops one user from deleting another user's quote, not
        // scope.
        services.AddAuthorization(options =>
        {
            options.AddPolicy("can-read-quotes", policy =>
                policy.RequireClaim("scope", "quotes.read"));

            options.AddPolicy("can-edit-quotes", policy =>
                policy.RequireClaim("scope", "quotes.write"));

            options.AddPolicy("can-delete-quotes", policy =>
                policy.RequireClaim("scope", "quotes.delete"));

            options.AddPolicy("can-read-collections", policy =>
                policy.RequireClaim("scope", "collections.read"));

            options.AddPolicy("can-edit-collections", policy =>
                policy.RequireClaim("scope", "collections.write"));

            options.AddPolicy("can-delete-collections", policy =>
                policy.RequireClaim("scope", "collections.delete"));
        });

        // Normalizes Entra ID's "scp"/"roles" claims into the same "scope"
        // claim shape our own tokens use, so the policies above work
        // identically no matter which scheme authenticated the caller.
        // See ScopeClaimsTransformation for why this is necessary.
        services.AddTransient<IClaimsTransformation, ScopeClaimsTransformation>();

        // Registers the resource-based ownership rule so it can be
        // resolved via IAuthorizationService.AuthorizeAsync(...) inside
        // the quote DELETE endpoint.
        services.AddSingleton<IAuthorizationHandler, MustOwnQuoteHandler>();

        // Day 19 -- Service Bus topic publisher + competing-consumer worker.
        // Enabled:false by default so unrelated tests never attempt AMQP.
        services.AddMessaging(configuration);

        // Day 21 -- the read-through cache and its instruments. Registered
        // BEFORE the DbContext is first resolved is not required, but it must
        // be registered before AddOutbox so the write service can take
        // IQuoteListCache. The metrics and the interceptor register whether or
        // not caching is enabled -- see CachingExtensions.
        services.AddCaching(configuration);

        // Day 20 -- the transactional outbox. Registered AFTER AddMessaging
        // because the relay resolves the IQuoteEventPublisher that call
        // registers, and registered unconditionally because the outbox WRITER
        // is part of the domain transaction rather than part of messaging.
        // Only the relay itself is behind a switch (Outbox:RelayEnabled).
        services.AddOutbox(configuration);

        return services;
    }
}
