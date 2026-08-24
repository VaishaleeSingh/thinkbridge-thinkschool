using QuotesApi.Configuration;

namespace QuotesApi.Extensions;

/// <summary>
/// The one CORS policy this API has (Day 13), kept in its own file for the
/// same reason every other cross-cutting concern here is: AddInfrastructure
/// is already long, and "which browser origins may call this API" is a
/// question someone will come looking for by name.
///
/// The policy is deliberately narrow. It names the origins instead of
/// allowing any, and lists the headers and methods the SPA actually uses
/// rather than allowing all of them, so widening it later is a visible,
/// reviewable edit instead of something that already happened.
/// </summary>
public static class CorsExtensions
{
    /// <summary>
    /// Referenced by name in Program.cs's UseCors call. A constant rather
    /// than a repeated string literal: a typo in either place produces a
    /// policy that silently never applies, and a browser error that says
    /// nothing about the cause.
    /// </summary>
    public const string SpaPolicyName = "spa";

    public static IServiceCollection AddSpaCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var corsOptions = configuration
            .GetSection(CorsOptions.SectionName)
            .Get<CorsOptions>() ?? new CorsOptions();

        var (allowedOrigins, malformedOrigins) = corsOptions.Partition();

        // Fail at startup, not at the first browser request. A malformed
        // entry ("http://localhost:4200/" with the trailing slash is the
        // one people actually write) never matches any origin, and the
        // only symptom is a CORS error in a browser console that blames
        // the API for not allowing an origin that configuration says it
        // does. This turns a confusing runtime symptom into a startup
        // message naming the offending value -- the same reasoning
        // JwtOptions' ValidateOnStart already follows.
        if (malformedOrigins.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins contains {malformedOrigins.Length} malformed " +
                $"origin(s): {string.Join(", ", malformedOrigins)}. Each entry must be " +
                "an absolute http/https origin with no path and no trailing slash, " +
                "e.g. \"http://localhost:4200\".");
        }

        services.AddCors(options =>
        {
            options.AddPolicy(SpaPolicyName, policy =>
            {
                // No origins configured -> a policy that allows nothing,
                // which is precisely how this API behaved before Day 13.
                // Nothing is registered on the builder, so every
                // cross-origin request is refused by default rather than
                // by omission.
                if (allowedOrigins.Length == 0)
                    return;

                policy
                    .WithOrigins(allowedOrigins)

                    // Authorization for the bearer token, Content-Type for
                    // the JSON bodies. Not AllowAnyHeader(): the preflight
                    // response is a statement about what this API accepts,
                    // and it may as well be an accurate one.
                    .WithHeaders("Authorization", "Content-Type")

                    // The verbs the endpoints actually expose.
                    .WithMethods("GET", "POST", "PUT", "DELETE")

                    // Preflight results are cacheable; without this the
                    // browser re-asks before every non-simple request.
                    .SetPreflightMaxAge(TimeSpan.FromHours(1));

                // Deliberately NOT .AllowCredentials(): this API is
                // authenticated by a bearer token the SPA sends in a
                // header, never by a cookie. Allowing credentials would
                // opt browsers into attaching cookies to these requests,
                // which is the shape CSRF needs and which nothing here
                // wants.
            });
        });

        return services;
    }
}
