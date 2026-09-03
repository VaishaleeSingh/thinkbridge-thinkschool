using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using QuotesApi.Caching;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Services;

/// <summary>
/// The transactional half of the outbox pattern.
///
/// Each method opens one transaction, writes the domain change and the outbox
/// row inside it, and commits both or neither. Nothing here touches the
/// broker; the relay does that, later, from the committed row.
///
/// WHY THERE ARE TWO SaveChangesAsync CALLS INSIDE ONE TRANSACTION, since a
/// single SaveChanges would be atomic by itself and would look tidier:
/// Quote.Id is database-generated, and QuoteChangedEvent.EventId is a
/// deterministic hash over (eventType, quoteId, occurredAt). The outbox row
/// therefore cannot be built until the quote's identity exists, which is after
/// the insert. Two saves, one explicit transaction, is the honest shape of
/// that constraint -- not an oversight.
///
/// WHY THE EXECUTION STRATEGY WRAPPER: it is already required, not a
/// precaution. SqlServerQuotesApiFactory calls
/// EnableRetryOnFailure(maxRetryCount: 3), so without this wrapper every write
/// in Quotes.Tests.Integration.SqlServer would throw
/// InvalidOperationException from BeginTransactionAsync -- a failure that
/// appears in that suite and in Azure and never locally on SQLite.
///
/// The app's own UseSqlServer call in InfrastructureExtensions does not enable
/// retries yet. When it does, this is already in place, and nobody has to
/// discover the requirement from a deployed environment.
///
/// The whole operation, including the retry wrapper, is idempotent from the
/// caller's point of view: a retried transaction re-executes the insert AND
/// the enqueue, and a rolled-back attempt leaves neither behind.
/// </summary>
public sealed class QuoteWriteService(
    QuotesDbContext db,
    IQuoteRepository repository,
    IOutboxWriter outbox,
    IOutboxSignal signal,
    IQuoteListCache listCache,
    IClock clock,
    ILogger<QuoteWriteService> logger) : IQuoteWriteService
{
    public async Task<Quote> CreateAsync(
        Quote quote,
        string? callerId,
        CancellationToken cancellationToken)
    {
        var created = await InTransactionAsync(
            async ct =>
            {
                var inserted = await repository.AddAsync(quote, ct);

                var evt = QuoteChangedEvent.Created(
                    inserted.Id, callerId, inserted.Author, inserted.Text, clock.UtcNow);

                var row = outbox.Enqueue(evt);

                // Second save, still inside the transaction opened below.
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Committed quote {QuoteId} with outbox row {OutboxId} for {EventType}",
                    inserted.Id, row.Id, evt.EventType);

                return inserted;
            },
            cancellationToken);

        // AFTER the commit, and only on success. Signalling inside the
        // transaction would wake the relay to look for a row that is not
        // visible yet, and would wake it even for a transaction that then
        // rolled back.
        signal.Notify();

        // Day 21 -- same argument, same place. A cache invalidated inside the
        // transaction would throw away a valid cache for a write that then
        // rolled back. A create can shift every subsequent page and changes
        // Total, so every cached page is stale, which is why this is a
        // generation bump rather than the removal of one key.
        await listCache.InvalidateAsync(cancellationToken);

        return created;
    }

    public async Task<Quote?> UpdateAsync(
        int id,
        string author,
        string text,
        string backgroundImageUrl,
        string? callerId,
        CancellationToken cancellationToken)
    {
        var updated = await InTransactionAsync(
            async ct =>
            {
                var quote = await repository.UpdateAsync(id, author, text, backgroundImageUrl, ct);

                if (quote is null)
                {
                    // Nothing was written, so nothing should be published.
                    // Returning early inside the transaction leaves it to be
                    // committed empty, which is cheaper and simpler than
                    // rolling back an operation that did nothing.
                    return null;
                }

                var evt = QuoteChangedEvent.Updated(
                    quote.Id, callerId, quote.Author, quote.Text, clock.UtcNow);

                outbox.Enqueue(evt);
                await db.SaveChangesAsync(ct);

                return quote;
            },
            cancellationToken);

        if (updated is not null)
        {
            signal.Notify();
            await listCache.InvalidateAsync(cancellationToken);
        }

        return updated;
    }

    public async Task<bool> DeleteAsync(
        int id,
        string? callerId,
        CancellationToken cancellationToken)
    {
        var deleted = await InTransactionAsync(
            async ct =>
            {
                var removed = await repository.DeleteAsync(id, ct);

                if (!removed)
                    return false;

                // QuoteDeleted carries no Author or Text: the row is gone, and
                // an event that described it would be describing state this
                // service can no longer vouch for.
                var evt = QuoteChangedEvent.Deleted(id, callerId, clock.UtcNow);

                outbox.Enqueue(evt);
                await db.SaveChangesAsync(ct);

                return true;
            },
            cancellationToken);

        if (deleted)
        {
            signal.Notify();
            await listCache.InvalidateAsync(cancellationToken);
        }

        return deleted;
    }

    /// <summary>
    /// Runs the operation inside one transaction, under the provider's
    /// execution strategy.
    ///
    /// Note the transaction is opened INSIDE the strategy's lambda, not around
    /// it. A retrying strategy re-invokes the lambda, so a transaction created
    /// outside would be reused after being rolled back -- which is precisely
    /// the mistake the strategy throws about.
    /// </summary>
    private async Task<TResult> InTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var result = await operation(ct);

                await transaction.CommitAsync(ct);

                return result;
            },
            cancellationToken);
    }
}
