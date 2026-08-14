using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using QuotesApi.Data;

namespace QuotesApi.Extensions;

/// <summary>
/// Health endpoints, added for Day 5's containerisation piece.
///
/// Three endpoints rather than one, because "is this container healthy?"
/// is really two unrelated questions with two different consequences:
///
///   /health/live   Is the process alive and able to answer HTTP at all?
///                  A failure here means the orchestrator should RESTART
///                  the container. So this check deliberately touches
///                  nothing -- no database, no outbound calls. If a slow
///                  database could fail the liveness probe, a database
///                  blip would restart every healthy replica at once and
///                  turn a recoverable problem into an outage.
///
///   /health/ready  Should this instance receive traffic right now?
///                  A failure means the load balancer should STOP SENDING
///                  requests, but leave the container running. That is the
///                  right response to a database that is briefly
///                  unreachable, so the database check lives here.
///
///   /health        Everything, which is what a human curls.
///
/// The distinction only pays off under an orchestrator, but getting it
/// wrong is the kind of thing that is discovered during an incident rather
/// than before one.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Tag applied to checks that must only affect readiness, never
    /// liveness. Used to filter which checks each endpoint runs.
    /// </summary>
    private const string ReadinessTag = "ready";

    public static IServiceCollection AddQuotesHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            // AddDbContextCheck calls CanConnectAsync on the context, which
            // is a genuine round trip to the database rather than an
            // inspection of the connection object. Tagged "ready" so it is
            // excluded from the liveness endpoint -- see the note above.
            .AddDbContextCheck<QuotesDbContext>(
                name: "database",
                tags: new[] { ReadinessTag });

        return services;
    }

    public static WebApplication MapQuotesHealthChecks(this WebApplication app)
    {
        // Liveness: run NO checks at all. The predicate returning false for
        // every registered check is the documented way to say "answer 200
        // if this process can serve a request, and consider nothing else".
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthResponseAsync
        });

        // Readiness: run only the checks tagged for it.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadinessTag),
            ResponseWriter = WriteHealthResponseAsync
        });

        // Everything. This is the endpoint the Day 5 task asks for.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponseAsync
        });

        return app;
    }

    /// <summary>
    /// The default response writer returns the single word "Healthy" with a
    /// text/plain content type. That is enough for a load balancer and not
    /// enough for a person: it cannot distinguish this application from any
    /// other application, or from a proxy answering on its behalf.
    ///
    /// This writer names the app and lists each check's result, so the
    /// response is evidence that the real thing is running rather than an
    /// assertion that something is.
    /// </summary>
    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            service = "QuotesApi",
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2),
                // Deliberately not entry.Value.Exception or .Description:
                // health endpoints are unauthenticated, and an exception
                // message from a failed database check is an excellent way
                // to hand a connection string to whoever asks. The status
                // is all an unauthenticated caller needs; the detail is in
                // the logs, correlated by TraceId.
                error = entry.Value.Exception is not null
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
