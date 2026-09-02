namespace QuotesApi.Models;

/// <summary>
/// One row per event that a committed domain change intends to publish.
///
/// Day 19 published straight from the request handler, after the write had
/// already committed, and caught every exception so the caller still got a
/// 201. That is the failure this table removes: a committed quote whose event
/// never reached the broker, with nothing left behind to replay it from.
///
/// The row is written INSIDE the same transaction as the domain change (see
/// QuoteWriteService), so the two cannot diverge: either the quote and the
/// intent to publish are both durable, or neither is.
///
/// What this does NOT provide is exactly-once delivery. The relay publishes
/// and then marks the row Sent -- two systems, no distributed transaction
/// between them -- so a crash in that gap republishes on restart. That is
/// deliberate and safe: the consumer side already dedupes on
/// (MessageId, SubscriptionName) -- see ProcessedMessage -- and MessageId
/// here is QuoteChangedEvent.EventId, which is a deterministic hash and so
/// is identical across restarts, not merely within one process.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// Database-generated, and the ONLY sequencer. The relay claims and
    /// publishes in Id order rather than by OccurredAtUtc: wall-clock
    /// timestamps tie at low resolution and skew between instances, so
    /// ordering by them would be ordering by something that is not actually
    /// ordered.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// QuoteChangedEvent.EventId, and the broker's MessageId when this row
    /// is sent. Unique: two requests that somehow derive the same logical
    /// event cannot both enqueue it, and the database says so rather than a
    /// code path hoping so.
    /// </summary>
    public required string MessageId { get; set; }

    /// <summary>
    /// Stored as a column, not dug out of Payload. The relay sets it on
    /// ApplicationProperties["eventType"], which is what the subscription
    /// SQL filters match on -- so the routing key must be readable without
    /// deserialising the body.
    /// </summary>
    public required string EventType { get; set; }

    /// <summary>
    /// The producer's contract version at the moment the row was written.
    /// A pending row can outlive a contract change; this lets the relay
    /// recognise one it can no longer build rather than crash-loop the batch.
    /// </summary>
    public required string SchemaVersion { get; set; }

    /// <summary>
    /// The serialised QuoteChangedEvent, frozen at write time.
    ///
    /// Deliberately a snapshot rather than a key the relay re-reads state
    /// from: an event describes what happened, and re-deriving it at publish
    /// time would publish whatever the row looks like NOW -- so two updates
    /// in quick succession would emit the later state twice and the earlier
    /// state never.
    /// </summary>
    public required string Payload { get; set; }

    /// <summary>
    /// W3C traceparent of the request that enqueued this row.
    ///
    /// Without it the trace breaks exactly where it is most worth reading:
    /// the relay publishes later, on another thread and possibly in another
    /// process, so Activity.Current at publish time has no relationship to
    /// the request that caused the change. The relay starts its span with
    /// this as the parent instead.
    /// </summary>
    public string? TraceParent { get; set; }

    public required DateTime OccurredAtUtc { get; set; }

    /// <summary>Pending | Sent | Failed. See <see cref="OutboxStatus"/>.</summary>
    public required string Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// Exception type and message, truncated. Never the payload: this column
    /// is read in diagnostics output and must not become a second, unaudited
    /// copy of user content.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Claim lease, not a boolean flag. A relay that is killed mid-batch must
    /// not leave its rows claimed forever; the lease expires and the next tick
    /// picks them up. That expiry is most of what the crash proof exercises.
    /// </summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Which relay instance holds the lease. Diagnostics only -- never an authorisation check.</summary>
    public string? LockOwner { get; set; }

    public DateTime? SentAtUtc { get; set; }
}

/// <summary>
/// Status values, as constants rather than an enum mapped to a string.
///
/// An enum would be tidier in C# and worse in the database: the value is read
/// by humans in diagnostics queries and by the retention sweep, and an enum
/// stored as an int makes both of those jobs require a lookup table that only
/// exists in source.
/// </summary>
public static class OutboxStatus
{
    /// <summary>Written and committed; not yet published.</summary>
    public const string Pending = "Pending";

    /// <summary>Confirmed accepted by the broker.</summary>
    public const string Sent = "Sent";

    /// <summary>
    /// Out of retry budget, or poison on the first attempt. Parked rather
    /// than retried forever, because one row that can never send would
    /// otherwise starve every good row behind it -- the producer-side
    /// equivalent of the dead-letter queue.
    /// </summary>
    public const string Failed = "Failed";
}
