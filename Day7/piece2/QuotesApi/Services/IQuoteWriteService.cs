using QuotesApi.Models;

namespace QuotesApi.Services;

/// <summary>
/// Every write to a quote that must also emit an event, behind one seam.
///
/// It exists for one reason: the transaction boundary belongs in exactly one
/// place. Before Day 20 each of the three endpoints called a repository and
/// then a publisher, so the atomicity rule was implemented three times and
/// enforced nowhere. The endpoints keep what they are good at -- validating a
/// request, checking ownership, shaping a response -- and lose the ability to
/// commit a change without recording the intent to publish it.
///
/// The visible consequence is that no endpoint takes an IQuoteEventPublisher
/// any more. Nothing on the request path can reach the broker at all.
/// </summary>
public interface IQuoteWriteService
{
    /// <summary>
    /// Inserts the quote and its QuoteCreated outbox row in one transaction.
    /// </summary>
    Task<Quote> CreateAsync(Quote quote, string? callerId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the update and its QuoteUpdated outbox row in one transaction.
    /// Returns null if the quote no longer exists, in which case nothing is
    /// written and no event is enqueued.
    /// </summary>
    Task<Quote?> UpdateAsync(
        int id,
        string author,
        string text,
        string backgroundImageUrl,
        string? callerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the quote and enqueues QuoteDeleted in one transaction.
    /// Returns false if it was already gone -- again with nothing written.
    /// </summary>
    Task<bool> DeleteAsync(int id, string? callerId, CancellationToken cancellationToken);
}
