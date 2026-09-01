using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>
/// Handles QuoteChanged events from the "audit" subscription.
/// Appends one <see cref="QuoteAuditEntry"/> row per event.
///
/// Idempotency: the handler does not check for duplicates itself.
/// Deduplication is owned by <see cref="QuoteEventProcessorService"/> via
/// <see cref="IProcessedMessageStore"/>.
///
/// The SaveChangesAsync below does NOT commit on its own. The processor opens
/// a transaction on this same scoped DbContext before calling the handler and
/// commits only after the dedupe row is written, so the audit row and the
/// record that it happened land together or not at all. That is what makes a
/// redelivery safe; a handler that committed independently would leave the
/// side effect behind whenever the dedupe INSERT lost a race.
/// </summary>
public sealed class AuditQuoteEventHandler(
    QuotesDbContext db,
    ILogger<AuditQuoteEventHandler> logger) : IQuoteEventHandler
{
    public string SubscriptionName => "audit";

    public async Task HandleAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        var entry = new QuoteAuditEntry
        {
            QuoteId = evt.QuoteId,
            EventType = evt.EventType,
            OwnerId = evt.OwnerId,
            OccurredAt = evt.OccurredAt,
            EventId = evt.EventId
        };

        db.QuoteAuditEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Audit: recorded {EventType} for quote {QuoteId} (EventId={EventId})",
            evt.EventType, evt.QuoteId, evt.EventId);
    }
}
