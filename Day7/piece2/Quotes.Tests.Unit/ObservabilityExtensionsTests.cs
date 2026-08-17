using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using QuotesApi.Extensions;

namespace Quotes.Tests.Unit;

/// <summary>
/// Guards the conditional wiring in AddObservability.
///
/// This is the highest-risk line in the observability setup and the reason
/// these tests exist: UseAzureMonitor() THROWS at startup when no connection
/// string is present, rather than degrading. CI has no connection string,
/// and the integration suite boots the whole app dozens of times per run --
/// so registering it unconditionally does not produce slightly noisier
/// output, it produces a red pipeline and a hundred-plus failing tests.
///
/// A future edit that "tidies up" the condition would break everything
/// loudly but somewhere far away from the change. This fails immediately,
/// in the right place, with the reason written next to it.
/// </summary>
public class ObservabilityExtensionsTests
{
    private static IConfiguration ConfigWith(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void AddObservability_WithNoExportersConfigured_DoesNotThrow()
    {
        // The CI and unit-test case: no OTLP collector, no App Insights.
        // Tracing should still be set up, exporting to nowhere.
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddObservability(ConfigWith());

        act.Should().NotThrow();
    }

    [Fact]
    public void AddObservability_WithAnOtlpEndpointButNoAppInsights_DoesNotThrow()
    {
        // The local-development case: Jaeger running, no Azure.
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddObservability(
            ConfigWith(("OpenTelemetry:OtlpEndpoint", "http://localhost:4317")));

        act.Should().NotThrow();
    }

    [Fact]
    public void AddObservability_IsSafeToResolve_WhenNothingIsConfigured()
    {
        // Registration succeeding is not the same as the container being
        // buildable: a misconfigured exporter typically fails when the
        // TracerProvider is first resolved, not when it is registered.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddObservability(ConfigWith());

        var act = () => services.BuildServiceProvider().GetService<TracerProvider>();

        act.Should().NotThrow();
    }
}
