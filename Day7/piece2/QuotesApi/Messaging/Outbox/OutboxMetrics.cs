using System.Diagnostics.Metrics;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// The relay's instruments. Registered with OpenTelemetry by name in
/// ObservabilityExtensions -- a meter that is not registered there emits
/// nothing, silently, exactly like an unregistered ActivitySource.
///
/// The gauges read cached values that the relay refreshes once per tick,
/// rather than querying the database from the observable callback. A callback
/// that issues a query runs on the metrics collection interval, on a thread
/// that has no scope and no cancellation, and turns a monitoring feature into
/// a source of load and of shutdown hangs.
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "QuotesApi.Outbox";

    private readonly Meter _meter;
    private readonly Counter<long> _published;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _parked;
    private readonly Histogram<double> _publishDuration;

    private volatile int _pendingCount;

    // NOT volatile: C# does not allow the modifier on double. Written with
    // Interlocked.Exchange instead, which gives the same publication
    // guarantee for the one thread that writes it and the collection thread
    // that reads it.
    private double _oldestPendingAgeSeconds;

    public OutboxMetrics()
    {
        _meter = new Meter(MeterName);

        _published = _meter.CreateCounter<long>(
            "outbox.published", "messages", "Outbox rows successfully published and marked Sent.");

        _failed = _meter.CreateCounter<long>(
            "outbox.publish.failures", "attempts", "Publish attempts that threw. Retried unless the budget is spent.");

        _parked = _meter.CreateCounter<long>(
            "outbox.parked", "messages", "Rows moved to Failed: out of attempts, or poison on the first try.");

        _publishDuration = _meter.CreateHistogram<double>(
            "outbox.publish.duration", "ms", "Time to send one row to the broker.");

        _meter.CreateObservableGauge(
            "outbox.pending.count", () => _pendingCount,
            "messages", "Rows awaiting publish.");

        // THE gauge to alert on. Pending count spikes normally under load and
        // makes a noisy alert; a row pending for minutes means the relay is
        // dead or wedged, which is the one condition under which this design
        // stops delivering without anything erroring.
        _meter.CreateObservableGauge(
            "outbox.oldest_pending.age", () => Interlocked.CompareExchange(ref _oldestPendingAgeSeconds, 0, 0),
            "s", "Age of the oldest pending row. Alert on this, not on the count.");
    }

    public void RecordPublished(int count = 1) => _published.Add(count);

    public void RecordFailure() => _failed.Add(1);

    public void RecordParked() => _parked.Add(1);

    public void RecordPublishDuration(double milliseconds) => _publishDuration.Record(milliseconds);

    public void SetBacklog(int pendingCount, double oldestPendingAgeSeconds)
    {
        _pendingCount = pendingCount;
        Interlocked.Exchange(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
    }

    public void Dispose() => _meter.Dispose();
}
