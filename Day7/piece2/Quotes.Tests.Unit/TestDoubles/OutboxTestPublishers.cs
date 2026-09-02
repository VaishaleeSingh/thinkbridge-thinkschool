using QuotesApi.Messaging;

namespace Quotes.Tests.Unit.TestDoubles;

/// <summary>
/// Records every event handed to it. The baseline: proves a row was published,
/// and how many times.
/// </summary>
public sealed class RecordingQuoteEventPublisher : IQuoteEventPublisher
{
    private readonly List<QuoteChangedEvent> _published = new();

    public IReadOnlyList<QuoteChangedEvent> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        lock (_published) _published.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Throws on the first <c>failuresBeforeSuccess</c> calls, then records.
/// Stands in for a broker outage: a transient fault that resolves.
/// </summary>
public sealed class FlakyQuoteEventPublisher(int failuresBeforeSuccess) : IQuoteEventPublisher
{
    private int _calls;
    private readonly List<QuoteChangedEvent> _published = new();

    public int Calls => _calls;

    public IReadOnlyList<QuoteChangedEvent> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);

        if (call <= failuresBeforeSuccess)
            throw new TimeoutException($"Simulated transient broker failure on attempt {call}.");

        lock (_published) _published.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records the send and THEN throws.
///
/// This is the crash-after-send simulation, and it is the closest thing a
/// test can honestly do to killing the process in the gap between
/// SendMessageAsync returning and the row being marked Sent. From the relay's
/// point of view the two are indistinguishable: the message is at the broker,
/// the row is still Pending, and the next pass will send it again.
///
/// The test asserts that duplicate happens. It is not a defect -- it is the
/// price of at-least-once, and Day 19's (MessageId, SubscriptionName) primary
/// key is what makes the SIDE EFFECT still happen exactly once.
/// </summary>
public sealed class SendThenCrashQuoteEventPublisher : IQuoteEventPublisher
{
    private readonly List<QuoteChangedEvent> _sent = new();

    public bool CrashAfterSend { get; set; } = true;

    public IReadOnlyList<QuoteChangedEvent> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        lock (_sent) _sent.Add(evt);

        if (CrashAfterSend)
            throw new IOException("Simulated process death after the message reached the broker.");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Always throws something the failure classifier calls poison. Used to prove
/// a row that can never succeed is parked on the FIRST attempt rather than
/// burning the whole retry budget and holding up the batch behind it.
/// </summary>
public sealed class PoisonQuoteEventPublisher : IQuoteEventPublisher
{
    public int Calls { get; private set; }

    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new System.Text.Json.JsonException("Simulated unrecoverable serialisation failure.");
    }
}

/// <summary>
/// Blocks until released. Lets a test hold one relay inside a publish while a
/// second relay tries to claim the same rows.
/// </summary>
public sealed class BlockingQuoteEventPublisher : IQuoteEventPublisher
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<QuoteChangedEvent> _published = new();

    public IReadOnlyList<QuoteChangedEvent> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    public void Release() => _gate.TrySetResult();

    public async Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        await _gate.Task;
        lock (_published) _published.Add(evt);
    }
}
