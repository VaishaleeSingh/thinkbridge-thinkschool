using System.Diagnostics.Metrics;

namespace QuotesApi.Resilience;

/// <summary>
/// The resilience pipeline's instruments, in the shape of Day 20's
/// OutboxMetrics and Day 21's CacheMetrics. Registered with OpenTelemetry by
/// name in ObservabilityExtensions -- a meter that is not registered there
/// emits nothing, silently.
///
/// WHY THESE COUNTERS AND NOT POLLY'S OWN. Polly v8 emits its own meter
/// ("Polly", counter resilience.polly.strategy.events, tagged with pipeline,
/// strategy and event names) and where that is available it is the better
/// source of truth, because it counts what the library actually did rather
/// than what our callbacks believe it did. These counters exist for the two
/// things it cannot tell us:
///
///   - resilience.retries.suppressed has no equivalent. A retry that was
///     DECLINED because the request was not idempotent is a non-event to
///     Polly: no retry occurred, so nothing is emitted. But it is exactly the
///     event Day 22 needs to see, because it is the only evidence that the
///     gate is doing anything at all rather than being satisfied by accident.
///
///   - resilience.circuit.state is a gauge read from the state provider. Event
///     counters record transitions; only a gauge answers "what is it now",
///     which is the question an operator actually asks.
///
/// WHAT THESE CANNOT ESTABLISH: none of them proves the dependency was spared.
/// A retry counter describes us. Whether an open circuit actually stopped
/// calling Entra ID is a statement about the outbound call, and the only
/// honest evidence for it is the absence of an outbound HTTP span in the trace
/// -- which is why the proof includes the Jaeger view and not only these
/// numbers. Same rule Day 21 applied to cache hit rate versus db.commands.
/// </summary>
public sealed class ResilienceMetrics : IDisposable
{
    public const string MeterName = "QuotesApi.Resilience";

    private readonly Meter _meter;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _retriesSuppressed;
    private readonly Counter<long> _circuitTransitions;
    private readonly Counter<long> _bulkheadRejections;

    private long _retryCount;
    private long _suppressedCount;
    private long _rejectionCount;
    private long _openedCount;
    private long _closedCount;
    private long _halfOpenedCount;

    public ResilienceMetrics(CircuitBreakerRegistry circuitBreaker)
    {
        _meter = new Meter(MeterName);

        _retries = _meter.CreateCounter<long>(
            "resilience.retries", "retries", "Retry attempts made against an outbound dependency.");

        _retriesSuppressed = _meter.CreateCounter<long>(
            "resilience.retries.suppressed", "requests",
            "Failed requests NOT retried because the request was not idempotent.");

        _circuitTransitions = _meter.CreateCounter<long>(
            "resilience.circuit.transitions", "transitions",
            "Circuit breaker state changes, tagged with the state entered.");

        _bulkheadRejections = _meter.CreateCounter<long>(
            "resilience.bulkhead.rejections", "requests",
            "Requests shed by the concurrency limiter rather than queued.");

        // The gauge, not a counter: "what state is it in", which is the
        // question asked during an incident. Read straight off the state
        // provider so it cannot drift from the breaker's actual state the way
        // a locally-tracked copy would.
        _meter.CreateObservableGauge(
            "resilience.circuit.state",
            () => circuitBreaker.StateAsGaugeValue,
            "state", "0 closed, 1 half-open, 2 open, 3 isolated.");
    }

    public long Retries => Interlocked.Read(ref _retryCount);

    public long RetriesSuppressed => Interlocked.Read(ref _suppressedCount);

    public long BulkheadRejections => Interlocked.Read(ref _rejectionCount);

    public long CircuitOpened => Interlocked.Read(ref _openedCount);

    public long CircuitClosed => Interlocked.Read(ref _closedCount);

    public long CircuitHalfOpened => Interlocked.Read(ref _halfOpenedCount);

    public void RecordRetry(string pipeline, string outcome)
    {
        Interlocked.Increment(ref _retryCount);
        _retries.Add(1,
            new KeyValuePair<string, object?>("pipeline", pipeline),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordSuppressedRetry(string pipeline, string method)
    {
        Interlocked.Increment(ref _suppressedCount);
        _retriesSuppressed.Add(1,
            new KeyValuePair<string, object?>("pipeline", pipeline),
            new KeyValuePair<string, object?>("method", method));
    }

    public void RecordCircuitOpened(string pipeline)
    {
        Interlocked.Increment(ref _openedCount);
        Transition(pipeline, "open");
    }

    public void RecordCircuitHalfOpened(string pipeline)
    {
        Interlocked.Increment(ref _halfOpenedCount);
        Transition(pipeline, "half-open");
    }

    public void RecordCircuitClosed(string pipeline)
    {
        Interlocked.Increment(ref _closedCount);
        Transition(pipeline, "closed");
    }

    public void RecordBulkheadRejection(string pipeline)
    {
        Interlocked.Increment(ref _rejectionCount);
        _bulkheadRejections.Add(1, new KeyValuePair<string, object?>("pipeline", pipeline));
    }

    private void Transition(string pipeline, string to) =>
        _circuitTransitions.Add(1,
            new KeyValuePair<string, object?>("pipeline", pipeline),
            new KeyValuePair<string, object?>("to", to));

    public void Dispose() => _meter.Dispose();
}
