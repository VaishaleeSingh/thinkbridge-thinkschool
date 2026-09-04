using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using QuotesApi.Resilience;

namespace Quotes.Tests.Unit.Resilience;

/// <summary>
/// THE DAY 22 DELIVERABLE: proof that the circuit opens under sustained
/// failure and recovers.
///
/// WHY THIS TEST DID NOT EXIST BEFORE. Day 5's test file says so in its own
/// words -- the breaker was skipped because opening it "needs at least
/// MinimumThroughput (10) failing calls inside a 30 second window and would
/// trade seconds of test runtime for a property that is a configured constant
/// rather than logic." Sound at the time, and the reason the most important
/// strategy in the pipeline went unproven for seventeen days: the breaker was
/// untestable BECAUSE its parameters were constants, and "it is only a
/// constant" then became the argument for not testing it. Binding the
/// parameters to configuration (ResilienceOptions) is what makes this file
/// possible; the whole sequence below runs in about two seconds.
///
/// WHY EVERY ASSERTION GOES THROUGH THE STATE PROVIDER. A test can only infer
/// a breaker's state from how often a stub handler was called -- and an open
/// breaker, a full bulkhead and a retry predicate that declined all produce
/// the identical call count. CircuitBreakerStateProvider reports the state
/// itself, so these are statements about the breaker rather than inferences
/// about it. Handler call counts are still asserted, but as the SEPARATE
/// claim they actually are: that an open circuit does not touch the
/// dependency.
///
/// WHY THE REQUESTS ARE POSTs. The breaker needs exactly one attempt per call
/// so the failure count is the call count. The obvious way to get that is
/// MaxRetryAttempts = 0, which Polly rejects (it validates the value as at
/// least 1). Sending a POST neutralises the retry through the Day 22
/// idempotency gate instead -- the request is not retryable, so each call is
/// one attempt -- which has the side benefit of exercising the gate in the
/// place where it matters. The circuit breaker itself is indifferent to the
/// method: a 503 is a failure whatever asked for it.
/// </summary>
public class CircuitBreakerLifecycleTests
{
    private static Task<HttpResponseMessage> PostAsync(HttpClient client) =>
        client.PostAsync(ResilienceTestHost.Url, content: null);

    [Fact]
    public async Task Circuit_StartsClosed()
    {
        var handler = new SwitchableHandler(HttpStatusCode.OK);
        using var provider = ResilienceTestHost.Build(
            handler, out _, ResilienceTestHost.FastBreaker());

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();

        breaker.State.CircuitState.Should().Be(CircuitState.Closed);

        var response = await PostAsync(provider.Client());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        breaker.State.CircuitState.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Circuit_UnderSustainedFailure_Opens()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out var logs, ResilienceTestHost.FastBreaker(minimumThroughput: 4));

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        var client = provider.Client();

        await DriveFailures(client, count: 6);

        breaker.State.CircuitState.Should().Be(CircuitState.Open);
        metrics.CircuitOpened.Should().Be(1);

        logs.Lines.Should().ContainSingle(l =>
            l.Level == LogLevel.Error && l.Message.Contains("circuit breaker OPENED"));
    }

    /// <summary>
    /// The negative, and it matters at least as much as the positive. Without
    /// it, the test above proves only that the breaker CAN open -- not that
    /// MinimumThroughput does anything. A breaker that opens on any two
    /// failures is the more damaging of the two misconfigurations, because it
    /// converts every blip into a self-inflicted outage of BreakDuration.
    /// </summary>
    [Fact]
    public async Task Circuit_WithFailuresBelowMinimumThroughput_StaysClosed()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out _, ResilienceTestHost.FastBreaker(minimumThroughput: 10));

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var client = provider.Client();

        // Every one of them fails. There are simply not enough of them for a
        // ratio to mean anything.
        await DriveFailures(client, count: 5);

        breaker.State.CircuitState.Should().Be(CircuitState.Closed);
        provider.GetRequiredService<ResilienceMetrics>().CircuitOpened.Should().Be(0);
    }

    /// <summary>
    /// The assertion most often skipped, and the one that decides whether this
    /// is a circuit breaker or just an error counter: while open, the
    /// dependency must not be called at all.
    /// </summary>
    [Fact]
    public async Task OpenCircuit_FailsFast_WithoutCallingTheDependency()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out _, ResilienceTestHost.FastBreaker(minimumThroughput: 4));

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var client = provider.Client();

        await DriveFailures(client, count: 6);
        breaker.State.CircuitState.Should().Be(CircuitState.Open);

        var attemptsBefore = handler.Attempts;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var act = async () => await PostAsync(client);

        await act.Should().ThrowAsync<BrokenCircuitException>();
        stopwatch.Stop();

        // The dependency was not touched. This is the whole point.
        handler.Attempts.Should().Be(attemptsBefore);

        // And it cost nothing. Asserted loosely on purpose -- the claim is an
        // order of magnitude ("microseconds, not a full attempt timeout"),
        // not a millisecond figure that would flake on a loaded CI agent. The
        // attempt timeout in this configuration is one second.
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    /// <summary>
    /// Half-open must admit ONE trial, not the herd. A breaker that lets
    /// everything through the moment BreakDuration elapses is how a recovering
    /// dependency gets knocked straight back down.
    /// </summary>
    [Fact]
    public async Task HalfOpenCircuit_AdmitsExactlyOneTrialRequest()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out _, ResilienceTestHost.FastBreaker(
                minimumThroughput: 4, breakDuration: "00:00:01"));

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        var client = provider.Client();

        await DriveFailures(client, count: 6);
        breaker.State.CircuitState.Should().Be(CircuitState.Open);

        var attemptsWhileOpen = handler.Attempts;

        // Past the break duration, with the dependency still broken.
        await Task.Delay(TimeSpan.FromMilliseconds(1400));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                try
                {
                    using var response = await PostAsync(client);
                    return response.StatusCode.ToString();
                }
                catch (BrokenCircuitException)
                {
                    return "rejected";
                }
            }));

        // Exactly one request reached the dependency; the other seven were
        // rejected by the still-open circuit.
        (handler.Attempts - attemptsWhileOpen).Should().Be(1);
        results.Count(r => r == "rejected").Should().Be(7);

        metrics.CircuitHalfOpened.Should().BeGreaterThanOrEqualTo(1);

        // The trial failed, so it goes straight back to open rather than
        // giving the herd a second chance.
        breaker.State.CircuitState.Should().Be(CircuitState.Open);
    }

    /// <summary>
    /// Recovery. Everything above is only half the requirement: a breaker that
    /// opens and never closes is a dependency permanently removed from the
    /// system by its own protection.
    /// </summary>
    [Fact]
    public async Task Circuit_WhenDependencyRecovers_ClosesAgain()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out var logs, ResilienceTestHost.FastBreaker(
                minimumThroughput: 4, breakDuration: "00:00:01"));

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        var client = provider.Client();

        await DriveFailures(client, count: 6);
        breaker.State.CircuitState.Should().Be(CircuitState.Open);

        // The dependency comes back BEFORE the trial request, which is the
        // ordering that matters: the breaker has no way to know it recovered
        // and must find out by letting one request through.
        handler.Returns(HttpStatusCode.OK);

        await Task.Delay(TimeSpan.FromMilliseconds(1400));

        using var trial = await PostAsync(client);

        trial.StatusCode.Should().Be(HttpStatusCode.OK);
        breaker.State.CircuitState.Should().Be(CircuitState.Closed);
        metrics.CircuitClosed.Should().Be(1);

        logs.Lines.Should().Contain(l =>
            l.Level == LogLevel.Information && l.Message.Contains("circuit breaker closed"));

        // And traffic flows normally afterwards, rather than the circuit
        // reporting Closed while still rejecting.
        using var afterwards = await PostAsync(client);
        afterwards.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The two instruments that could disagree, asserted to agree once. The
    /// state provider and the OnOpened/OnClosed callbacks are independent
    /// paths out of the same strategy; if they ever diverge, every other
    /// assertion in this file is reading one of two different truths.
    /// </summary>
    [Fact]
    public async Task StateProvider_AndTransitionCounters_Agree()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out _, ResilienceTestHost.FastBreaker(
                minimumThroughput: 4, breakDuration: "00:00:01"));

        var breaker = provider.GetRequiredService<CircuitBreakerRegistry>();
        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        var client = provider.Client();

        await DriveFailures(client, count: 6);

        breaker.State.CircuitState.Should().Be(CircuitState.Open);
        breaker.StateAsGaugeValue.Should().Be(2);
        metrics.CircuitOpened.Should().Be(1);
        metrics.CircuitClosed.Should().Be(0);

        handler.Returns(HttpStatusCode.OK);
        await Task.Delay(TimeSpan.FromMilliseconds(1400));
        using var trial = await PostAsync(client);

        breaker.State.CircuitState.Should().Be(CircuitState.Closed);
        breaker.StateAsGaugeValue.Should().Be(0);
        metrics.CircuitClosed.Should().Be(1);
    }

    /// <summary>
    /// Sends failing requests one at a time, swallowing whichever way the
    /// pipeline reports the failure. Sequential rather than concurrent so the
    /// breaker sees a countable number of outcomes -- a concurrent burst can
    /// have several calls in flight when the circuit opens, which makes the
    /// failure count an approximation.
    /// </summary>
    private static async Task DriveFailures(HttpClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            try
            {
                using var response = await PostAsync(client);
            }
            catch (BrokenCircuitException)
            {
                // Already open. Expected once the threshold is crossed.
            }
        }
    }
}
