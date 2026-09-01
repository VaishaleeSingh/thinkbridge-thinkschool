namespace QuotesApi.Models;

/// <summary>
/// Tracks which (MessageId, SubscriptionName) pairs have already been
/// processed, providing consumer-side idempotency for at-least-once delivery.
///
/// Composite primary key: (MessageId, SubscriptionName).
/// The two subscriptions receive the SAME MessageId for a given publish;
/// a single-column key would let one subscription's row suppress the other's.
///
/// The PRIMARY KEY constraint is the actual guarantee under concurrency:
/// two concurrent handlers both reading "not seen" can both reach this INSERT,
/// and exactly one will succeed. The loser gets a DbUpdateException with a
/// unique-constraint error code; the processor treats that as "already done".
///
/// Retention: rows should be cleaned up on a schedule after a window longer
/// than the maximum plausible redelivery delay (message TTL + DLQ dwell time).
/// Rows that age out before replay could silently break the dedupe guarantee.
///
/// Index on ProcessedAtUtc supports the cleanup query efficiently.
/// </summary>
public sealed class ProcessedMessage
{
    public required string MessageId { get; set; }
    public required string SubscriptionName { get; set; }
    public required DateTime ProcessedAtUtc { get; set; }
    public required string Outcome { get; set; }
}
