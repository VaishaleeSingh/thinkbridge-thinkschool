namespace QuotesApi.Messaging;

/// <summary>
/// Tracks which messages have already been processed by a given subscription.
///
/// The composite key (MessageId, SubscriptionName) is critical: two
/// subscriptions receive the same MessageId from one publish, and they are
/// different pieces of work. A single-column key would let the audit handler's
/// row suppress the search-index handler's work.
///
/// The guarantee is the PRIMARY KEY CONSTRAINT, not an application-level
/// check-then-act: two concurrent handlers can both read "not seen" and both
/// proceed. The second INSERT hits a unique-constraint violation, which the
/// caller catches and treats as "already processed". A cheap pre-check is fine
/// as an optimisation but must not be mistaken for the guarantee.
/// </summary>
public interface IProcessedMessageStore
{
    /// <summary>
    /// Returns true if this (messageId, subscriptionName) pair was already
    /// processed. Cheap read-path optimisation; the real guarantee is in
    /// <see cref="RecordAsync"/>.
    /// </summary>
    Task<bool> HasSeenAsync(string messageId, string subscriptionName, CancellationToken ct = default);

    /// <summary>
    /// Inserts a ProcessedMessages row inside the CALLER'S transaction.
    /// If the row already exists (concurrent or redelivered message) an
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> will be
    /// thrown with the provider's unique-constraint error code. The processor
    /// service catches that specific error and completes the message silently.
    /// </summary>
    Task RecordAsync(string messageId, string subscriptionName, string outcome, CancellationToken ct = default);
}
