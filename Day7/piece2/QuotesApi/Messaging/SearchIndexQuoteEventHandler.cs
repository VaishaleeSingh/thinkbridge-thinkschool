using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>
/// Handles QuoteChanged events from the "search-index" subscription.
///
/// The subscription SQL filter is:
///   eventType IN ('QuoteCreated','QuoteUpdated')
/// Delete events are intentionally excluded — you cannot update a
/// search-index entry for something you no longer have. This makes the
/// filter observable: publish three events, audit sees 3, search-index
/// sees 2.
///
/// Side effect: upserts a <see cref="QuoteSearchProjection"/> row.
/// </summary>
public sealed class SearchIndexQuoteEventHandler(
    QuotesDbContext db,
    ILogger<SearchIndexQuoteEventHandler> logger) : IQuoteEventHandler
{
    public async Task HandleAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        // Upsert: fetch existing or create new.
        var projection = await db.QuoteSearchProjections
            .FindAsync(new object[] { evt.QuoteId }, cancellationToken);

        if (projection is null)
        {
            projection = new QuoteSearchProjection { QuoteId = evt.QuoteId };
            db.QuoteSearchProjections.Add(projection);
        }

        projection.Author = evt.Author ?? projection.Author;
        projection.Text = evt.Text ?? projection.Text;
        projection.LastUpdatedAt = evt.OccurredAt;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SearchIndex: upserted projection for quote {QuoteId} (EventType={EventType}, EventId={EventId})",
            evt.QuoteId, evt.EventType, evt.EventId);
    }
}
