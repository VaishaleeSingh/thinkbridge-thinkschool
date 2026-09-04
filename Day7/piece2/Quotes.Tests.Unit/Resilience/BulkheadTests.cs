using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using QuotesApi.Resilience;

namespace Quotes.Tests.Unit.Resilience;

/// <summary>
/// The bulkhead.
///
/// Polly v8 has no strategy called "bulkhead" -- v7's BulkheadPolicy was
/// replaced by the rate-limiter strategy over System.Threading.RateLimiting,
/// and a bulkhead is that strategy configured with a ConcurrencyLimiter. Same
/// semantics: a cap on simultaneous executions plus a bounded queue.
///
/// Three properties are worth a test, and the second and third are the ones
/// that turn a limiter into a bulkhead rather than a new failure mode.
/// </summary>
public class BulkheadTests
{
    private static Dictionary<string, string?> OnePermit()
    {
        // MinimumThroughput high enough that the breaker cannot participate:
        // this file is about the limiter, and a breaker opening midway would
        // make the assertions ambiguous.
        var settings = ResilienceTestHost.FastBreaker(minimumThroughput: 1000);
        settings["Resilience:Bulkhead:PermitLimit"] = "1";
        settings["Resilience:Bulkhead:QueueLimit"] = "0";
        return settings;
    }

    [Fact]
    public async Task WhenAllPermitsAreHeld_ExcessRequestsAreShed_NotQueued()
    {
        var handler = new BlockingHandler();
        using var provider = ResilienceTestHost.Build(handler, out _, OnePermit());

        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var client = provider.Client();

        // Hold the only permit.
        var inFlight = client.GetAsync(ResilienceTestHost.Url);
        await handler.Entered;

        var act = async () => await client.GetAsync(ResilienceTestHost.Url);

        await act.Should().ThrowAsync<RateLimiterRejectedException>();

        // The shed request never reached the dependency -- which is the point.
        // Shedding at the edge is cheap; shedding after the connection is
        // established is not shedding.
        handler.Attempts.Should().Be(1);
        metrics.BulkheadRejections.Should().Be(1);

        // NOT RETRIED. The limiter sits outside the retry, so a rejection
        // cannot land in the retry's ShouldHandle. Retrying a load-shed
        // rejection is the definition of making an overload worse, and this is
        // the assertion that the ordering in ResilienceExtensions is the one
        // documented there.
        metrics.Retries.Should().Be(0);

        // NOT A DEPENDENCY FAILURE. The limiter also sits outside the breaker,
        // so our own back-pressure can never open the circuit. Structural
        // rather than a predicate someone has to remember to write -- and
        // worth asserting precisely because it is invisible in the code.
        breaker.State.CircuitState.Should().Be(CircuitState.Closed);
        metrics.CircuitOpened.Should().Be(0);

        handler.Release();
        using var completed = await inFlight;
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WhenQueueHasRoom_ExcessRequestsWait_RatherThanFail()
    {
        var handler = new BlockingHandler();
        var settings = OnePermit();
        settings["Resilience:Bulkhead:QueueLimit"] = "4";

        using var provider = ResilienceTestHost.Build(handler, out _, settings);
        var client = provider.Client();

        var first = client.GetAsync(ResilienceTestHost.Url);
        await handler.Entered;

        var queued = client.GetAsync(ResilienceTestHost.Url);

        // Still waiting, not rejected: QueueLimit is what distinguishes "shed
        // immediately" from "briefly absorb a burst".
        queued.IsCompleted.Should().BeFalse();

        handler.Release();

        using var firstResponse = await first;
        using var queuedResponse = await queued;

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        queuedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        provider.GetRequiredService<ResilienceMetrics>().BulkheadRejections.Should().Be(0);
    }

    /// <summary>
    /// A limiter that leaks permits degrades to PermitLimit=0, and the symptom
    /// is total, permanent failure of the dependency with nothing in the logs
    /// to explain it. Failing requests are the case worth checking, because a
    /// permit released only on the success path is the shape the bug takes.
    /// </summary>
    [Fact]
    public async Task Permits_AreReleased_EvenWhenTheRequestFails()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        var settings = OnePermit();
        settings["Resilience:Retry:MaxAttempts"] = "1";

        using var provider = ResilienceTestHost.Build(handler, out _, settings);
        var client = provider.Client();

        // Sequentially, so each call has the single permit to itself. If a
        // permit were leaked on the failure path, the second call would be
        // rejected instead of failing on its own merits.
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.GetAsync(ResilienceTestHost.Url);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        provider.GetRequiredService<ResilienceMetrics>().BulkheadRejections.Should().Be(0);

        handler.Returns(HttpStatusCode.OK);
        using var recovered = await client.GetAsync(ResilienceTestHost.Url);
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
