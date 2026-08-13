using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuotesApi.Observability;

namespace QuotesApi.Extensions;

/// <summary>
/// Distributed tracing, and shipping it somewhere. Kept separate from
/// InfrastructureExtensions, which is already long and is about what the app
/// needs in order to serve a request; this is about being able to see what
/// happened afterward.
///
/// There are two possible destinations, and they are independent:
///   - an OTLP collector (Jaeger / Aspire) for local development
///   - Azure Monitor / Application Insights for deployed environments
/// Both, either, or neither can be active. Spans are created regardless --
/// an exporter only decides where they are sent, so with neither configured
/// the app still runs and still correlates logs to trace IDs, it just keeps
/// the telemetry to itself.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // WHY BOTH EXPORTERS ARE CONDITIONAL:
        //
        // AddOtlpExporter() with no collector listening does not fail
        // quietly -- it keeps retrying against localhost:4317 and logging
        // the failures. UseAzureMonitor() is worse: with no connection
        // string it THROWS at startup rather than degrading.
        //
        // Neither is configured in CI or in the test suite, and the
        // integration tests boot this app dozens of times per run. Wiring
        // either one unconditionally would mean a broken pipeline and 135
        // failing tests, not a slightly noisier log.
        var otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];
        var appInsightsConnectionString = configuration["ApplicationInsights:ConnectionString"];
        var azureMonitorEnabled = !string.IsNullOrWhiteSpace(appInsightsConnectionString);

        var openTelemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: "QuotesApi"));

        openTelemetry.WithTracing(tracing =>
        {
            // Neither of these is provided by the Azure Monitor distro, so
            // they are always ours to register.
            tracing
                .AddEntityFrameworkCoreInstrumentation()
                .AddSource(QuotesActivitySource.Name);

            // ...whereas these two ARE part of the distro. Registering them
            // here as well when Azure Monitor is active would instrument the
            // same events twice: every request and every outbound call would
            // be recorded as two spans, which silently corrupts every
            // duration percentile and doubles the ingestion bill. So they
            // are added only when the distro is not doing it for us.
            if (!azureMonitorEnabled)
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            }

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        });

        if (azureMonitorEnabled)
        {
            // Exports traces, metrics AND logs to Application Insights.
            // Logs arrive through the ILoggerProvider this registers, which
            // is why Program.cs passes writeToProviders: true to Serilog --
            // without it Serilog would swallow everything before it ever
            // reached this provider.
            openTelemetry.UseAzureMonitor(options =>
                options.ConnectionString = appInsightsConnectionString);
        }

        return services;
    }
}
