namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// Wakes the relay immediately after a commit, so an event's latency is
/// milliseconds instead of a coin-flip up to the poll interval.
///
/// It is an OPTIMISATION and nothing more. The relay waits on the signal OR
/// the poll interval, whichever comes first, so a signal that is dropped
/// (queue full), lost (process restart) or never raised at all (a row written
/// by another instance, or by a migration) costs one poll interval -- never a
/// message. If the signal were the only trigger it would be a second,
/// in-memory publish path with exactly the durability the outbox exists to
/// replace.
/// </summary>
public interface IOutboxSignal
{
    /// <summary>Non-blocking, never throws, safe to call from a request path.</summary>
    void Notify();

    /// <summary>
    /// Completes when a notification arrives or <paramref name="timeout"/>
    /// elapses. Returns true if it was woken by a notification -- used only
    /// for logging; the relay's behaviour is identical either way.
    /// </summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
