using FluentAssertions;
using QuotesApi.Resilience;

namespace Quotes.Tests.Unit.Resilience;

/// <summary>
/// The predicate on its own, as a pure function of a request.
///
/// It is tested separately from the pipeline for the same reason
/// AuthSchemeSelector is: the decision is a small piece of logic with a lot of
/// cases, and enumerating them through an HTTP pipeline would cost a
/// round trip per case to test something that never touches the network.
/// </summary>
public class IdempotencyPredicateTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IdempotentMethods_AreRetryable(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://example.test/");

        IdempotencyPredicate.IsRetryable(request).Should().BeTrue();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    public void NonIdempotentMethods_AreNotRetryable(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://example.test/");

        IdempotencyPredicate.IsRetryable(request).Should().BeFalse();
    }

    [Fact]
    public void Post_WithAnIdempotencyKey_IsRetryable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");
        request.Headers.Add(IdempotencyPredicate.IdempotencyKeyHeader, "a-key");

        // Presence is the whole signal. The value is the far end's
        // deduplication key and means nothing to us.
        IdempotencyPredicate.IsRetryable(request).Should().BeTrue();
    }

    /// <summary>
    /// The direction this fails in is a decision, not an accident.
    ///
    /// An exception outcome carries no HttpResponseMessage, so the request is
    /// not always reachable from a Polly predicate. When the pipeline cannot
    /// see what it is about to repeat, "retry the unknown" is the wrong side to
    /// fail to: under-retrying costs latency on one request, over-retrying
    /// costs a duplicate write.
    /// </summary>
    [Fact]
    public void AnUnknownRequest_IsNotRetryable()
    {
        IdempotencyPredicate.IsRetryable(null).Should().BeFalse();
    }

    [Fact]
    public void CustomMethods_AreNotAssumedIdempotent()
    {
        using var request = new HttpRequestMessage(new HttpMethod("MERGE"), "https://example.test/");

        IdempotencyPredicate.IsRetryable(request).Should().BeFalse();
    }
}
