using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>
/// EF Core implementation of <see cref="IProcessedMessageStore"/>.
///
/// The composite primary key on (MessageId, SubscriptionName) in the
/// database is the actual idempotency guarantee. The unique constraint
/// means a second INSERT for the same pair throws a
/// <see cref="DbUpdateException"/> — the caller (<see cref="QuoteEventProcessorService"/>)
/// catches that specific error and completes the message without repeating
/// the side effect.
///
/// Retention: rows grow forever without a cleanup job. The cleanup query
/// (delete where ProcessedAtUtc &lt; retention window) should run on a
/// schedule. The window must exceed the maximum plausible redelivery delay
/// — including the message's TTL and any DLQ dwell time — or the dedupe
/// guarantee silently lapses for old messages.
/// </summary>
public sealed class EfProcessedMessageStore(QuotesDbContext db) : IProcessedMessageStore
{
    public async Task<bool> HasSeenAsync(
        string messageId,
        string subscriptionName,
        CancellationToken ct = default)
    {
        return await db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(
                m => m.MessageId == messageId && m.SubscriptionName == subscriptionName,
                ct);
    }

    public async Task RecordAsync(
        string messageId,
        string subscriptionName,
        string outcome,
        CancellationToken ct = default)
    {
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            SubscriptionName = subscriptionName,
            ProcessedAtUtc = DateTime.UtcNow,
            Outcome = outcome
        });

        // Deliberately NOT catching here. The caller owns the transaction
        // and must catch the unique-constraint violation to correctly
        // distinguish "already processed" from any other DbUpdateException.
        await db.SaveChangesAsync(ct);
    }
}
