namespace QuotesApi.Resilience;

/// <summary>
/// Names for the pipeline and its strategies.
///
/// These are not decoration. Polly v8 tags every telemetry event it emits with
/// the pipeline name and the strategy name, so an unnamed strategy produces
/// metrics that cannot be attributed to it -- you can see that something
/// retried, not what. The tests also assert on these names, which means a
/// rename cannot silently detach a dashboard from the thing it was watching.
///
/// "bulkhead" is kept as a name even though Polly v8 has no bulkhead strategy.
/// The v7 BulkheadPolicy was replaced by the rate-limiter strategy over
/// System.Threading.RateLimiting, and a bulkhead is that strategy configured
/// with a ConcurrencyLimiter: same semantics -- a cap on simultaneous
/// executions plus a bounded queue -- under a different name. Anyone grepping
/// this repository for "bulkhead" after reading the Day 22 task should find it.
/// </summary>
public static class ResiliencePipelineNames
{
    public const string EntraIdPipeline = "entra-id";

    public const string TotalTimeout = "total-timeout";
    public const string Bulkhead = "bulkhead";
    public const string Retry = "retry";
    public const string CircuitBreaker = "circuit-breaker";
    public const string AttemptTimeout = "attempt-timeout";
}
