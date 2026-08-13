using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuotesApi.Observability;

namespace QuotesApi.Extensions;

/// <summary>
/// Distributed tracing. Kept separate from InfrastructureExtensions, which
/// is already long and is about what the app needs in order to serve a
/// request (database, repositories, services, auth); this is about being
/// able to see what happened afterward.
///
/// Every request now produces a trace: a span for the request itself, a
/// nested span per EF Core query, a nested span per outbound HttpClient
/// call, plus whatever custom spans the app starts from
/// QuotesActivitySource. Because OpenTelemetry and Serilog both read the
/// same ambient System.Diagnostics.Activity, the TraceId on a log line and
/// the TraceId of the trace are the same value -- so a log line found in
/// the console leads directly to the trace for that request, and back.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // WHY THE EXPORTER IS CONDITIONAL:
        // AddOtlpExporter() with no collector listening does not fail
        // quietly -- it keeps trying to POST to localhost:4317, retries,
        // and logs the failures. Nothing in the test suite or in CI runs a
        // collector, and the integration tests boot this app dozens of
        // times per run, so an unconditional exporter would mean dozens of
        // rounds of connection errors drowning the output of every test
        // run for no benefit. Spans are still created and still correlate
        // with logs without it; the exporter is only about shipping them
        // somewhere. Set OpenTelemetry:OtlpEndpoint (see
        // appsettings.Development.json) when a Jaeger or Aspire dashboard
        // is actually running.
        var otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: "QuotesApi"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(QuotesActivitySource.Name);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
            });

        return services;
    }
}
