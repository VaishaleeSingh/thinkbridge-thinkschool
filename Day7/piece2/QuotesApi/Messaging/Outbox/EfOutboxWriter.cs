using System.Diagnostics;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// EF implementation of <see cref="IOutboxWriter"/>.
///
/// Scoped, and that matters: it must resolve the very same QuotesDbContext
/// instance the caller's transaction is running on. Registered as a singleton
/// it would either capture a disposed context or need its own, and in both
/// cases the row would leave the transaction the pattern depends on.
/// </summary>
public sealed class EfOutboxWriter(QuotesDbContext db) : IOutboxWriter
{
    public OutboxMessage Enqueue(QuoteChangedEvent evt)
    {
        var row = new OutboxMessage
        {
            MessageId = evt.EventId,
            EventType = evt.EventType,
            SchemaVersion = evt.SchemaVersion,

            // Serialised HERE, at write time, not at publish time. See the
            // Payload comment on OutboxMessage for why re-deriving it later
            // would publish the wrong state.
            Payload = OutboxPayload.Serialize(evt),

            // Captured while the request's Activity is still current. Read in
            // the relay this would be null, or worse, some unrelated span.
            TraceParent = Activity.Current?.Id,

            OccurredAtUtc = evt.OccurredAt.UtcDateTime,
            Status = OutboxStatus.Pending,
            Attempts = 0
        };

        db.OutboxMessages.Add(row);

        // Deliberately no SaveChangesAsync. See IOutboxWriter.
        return row;
    }
}
