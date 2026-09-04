using System.Net.Http;

namespace QuotesApi.Resilience;

/// <summary>
/// Decides whether a request may be retried at all, independently of whether
/// its failure looked transient.
///
/// WHY THIS IS A DEFECT FIX AND NOT A NEW FEATURE.
/// Day 5 left HttpRetryStrategyOptions.ShouldHandle at its default, which
/// handles 5xx, 408, HttpRequestException and inner-timeout cancellations --
/// and does so regardless of HTTP method. On the entra-id client that is
/// harmless, because every request the JwtBearer handler issues is a GET. But
/// that is a property of today's only caller, not of the pipeline: the
/// pipeline is a reusable registration, and the first POST routed through it
/// inherits a policy that will re-send a write after a 503. A 503 does not
/// tell you whether the far end processed the request before it fell over, so
/// the retry is a coin flip on a duplicate.
///
/// The gate therefore lives in the policy, not in the caller's good manners.
///
/// WHAT "IDEMPOTENT" MEANS HERE, precisely, because the word is doing real
/// work: RFC 9110 defines GET, HEAD, OPTIONS, TRACE, PUT and DELETE as
/// idempotent methods -- repeating the request has the same effect on the
/// server as making it once. POST and PATCH are not.
///
/// THE ASSUMPTION THIS INHERITS, stated so the next person routing a write
/// through this client knows they are inheriting it: PUT and DELETE are
/// idempotent by SPECIFICATION, not by implementation. A DELETE that returns
/// 404 the second time, or a PUT that appends rather than replaces, is not
/// idempotent whatever its method says. A generic pipeline has no way to know
/// that; the method is the only contract available to it. Anything that needs
/// a stronger guarantee should carry an Idempotency-Key and have the far end
/// honour it.
/// </summary>
public static class IdempotencyPredicate
{
    /// <summary>
    /// The opt-in header. A POST carrying one is asserting that the far end
    /// deduplicates on it, which makes the POST safe to repeat.
    /// </summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// True when this request may be retried.
    ///
    /// A NULL REQUEST RETURNS FALSE, and that direction is chosen on purpose.
    /// The request is not always reachable from a Polly predicate -- an
    /// exception outcome carries no HttpResponseMessage, so
    /// Outcome.Result?.RequestMessage is null and the request has to come from
    /// the ResilienceContext instead. If neither is available, the pipeline
    /// does not know what it is about to repeat, and "retry something unknown"
    /// is the wrong side to fail to. Under-retrying costs latency on one
    /// request; over-retrying costs a duplicate write.
    /// </summary>
    public static bool IsRetryable(HttpRequestMessage? request)
    {
        if (request is null)
            return false;

        if (IsIdempotentMethod(request.Method))
            return true;

        // The explicit opt-in. Presence is the whole signal: the value is the
        // far end's deduplication key and means nothing to us.
        return request.Headers.Contains(IdempotencyKeyHeader);
    }

    public static bool IsIdempotentMethod(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Options
        || method == HttpMethod.Trace
        || method == HttpMethod.Put
        || method == HttpMethod.Delete;
}
