using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuotesApi.Resilience;

namespace Quotes.Tests.Unit.Resilience;

/// <summary>
/// The idempotency gate, end to end through the real pipeline.
///
/// WHAT IS BEING FIXED HERE, because it is a defect and not a new feature:
/// Day 5 left HttpRetryStrategyOptions.ShouldHandle at its default, which
/// handles 5xx / 408 / HttpRequestException / inner timeouts regardless of
/// HTTP METHOD. On the entra-id client that is harmless, because the only
/// caller -- the JwtBearer metadata fetch -- issues GETs. But that is a
/// property of today's caller, not of the pipeline: the pipeline is a reusable
/// registration, and the first POST routed through it would inherit a policy
/// that re-sends a write after a 503. A 503 does not say whether the far end
/// processed the request before it fell over, so the retry is a coin flip on
/// a duplicate.
///
/// Every test here uses a breaker with a very high MinimumThroughput, so the
/// circuit cannot open partway through and turn a retry assertion into a
/// breaker assertion. Isolating one strategy at a time is the only way the
/// attempt counts mean what they claim to mean.
/// </summary>
public class RetryGateTests
{
    private static Dictionary<string, string?> RetryOnly(bool idempotentOnly = true)
    {
        var settings = ResilienceTestHost.FastBreaker(minimumThroughput: 1000);
        settings["Resilience:Retry:MaxAttempts"] = "3";
        settings["Resilience:Retry:IdempotentOnly"] = idempotentOnly ? "true" : "false";
        return settings;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task IdempotentMethod_OnTransientFailure_IsRetried(string method)
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(handler, out _, RetryOnly());

        using var request = new HttpRequestMessage(new HttpMethod(method), ResilienceTestHost.Url);
        using var response = await provider.Client().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // One original attempt plus three retries.
        handler.Attempts.Should().Be(4);
        provider.GetRequiredService<ResilienceMetrics>().Retries.Should().Be(3);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    public async Task NonIdempotentMethod_OnTransientFailure_IsNotRetried(string method)
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(handler, out var logs, RetryOnly());

        using var request = new HttpRequestMessage(new HttpMethod(method), ResilienceTestHost.Url);
        using var response = await provider.Client().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Exactly one call reached the dependency. The failure was transient,
        // and the pipeline declined to repeat it anyway.
        handler.Attempts.Should().Be(1);

        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        metrics.Retries.Should().Be(0);

        // The counter that exists BECAUSE a declined retry is a non-event to
        // Polly: no retry happened, so its own telemetry emits nothing, and a
        // broken gate would look exactly like a gate that never triggers.
        metrics.RetriesSuppressed.Should().Be(1);

        logs.Lines.Should().Contain(l =>
            l.Level == LogLevel.Warning
            && l.Message.Contains("NOT retried")
            && l.Message.Contains(method));
    }

    [Fact]
    public async Task Post_WithIdempotencyKey_IsRetried()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(handler, out _, RetryOnly());

        using var request = new HttpRequestMessage(HttpMethod.Post, ResilienceTestHost.Url);
        request.Headers.Add(IdempotencyPredicate.IdempotencyKeyHeader, Guid.NewGuid().ToString());

        using var response = await provider.Client().SendAsync(request);

        // The caller has asserted that the far end deduplicates on the key,
        // which is what makes repeating the POST safe.
        handler.Attempts.Should().Be(4);
        provider.GetRequiredService<ResilienceMetrics>().RetriesSuppressed.Should().Be(0);
    }

    [Fact]
    public async Task WhenGateIsDisabled_PostIsRetried_AndTheChoiceIsLogged()
    {
        var handler = new SwitchableHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = ResilienceTestHost.Build(
            handler, out var logs, RetryOnly(idempotentOnly: false));

        using var response = await provider.Client().PostAsync(ResilienceTestHost.Url, content: null);

        handler.Attempts.Should().Be(4);

        // Switching the gate off is a decision to risk duplicate writes. It is
        // logged at startup so that whoever has to explain the duplicate later
        // can find the decision in the logs of the process that made it.
        logs.Lines.Should().Contain(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("IdempotentOnly is false"));
    }

    /// <summary>
    /// The Day 5 property, preserved. A 404 is an answer, not a blip -- for
    /// any method. The gate must not have quietly widened what gets retried.
    /// </summary>
    [Fact]
    public async Task ClientError_IsNotRetried_EvenForAnIdempotentMethod()
    {
        var handler = new SwitchableHandler(HttpStatusCode.NotFound);
        using var provider = ResilienceTestHost.Build(handler, out _, RetryOnly());

        using var response = await provider.Client().GetAsync(ResilienceTestHost.Url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Attempts.Should().Be(1);

        var metrics = provider.GetRequiredService<ResilienceMetrics>();
        metrics.Retries.Should().Be(0);

        // Not "suppressed" either: nothing was suppressed, the failure simply
        // was not transient. Conflating the two would make the gate's counter
        // meaningless.
        metrics.RetriesSuppressed.Should().Be(0);
    }
}
