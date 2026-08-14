using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class CollectionRepository : ICollectionRepository
{
    private readonly QuotesDbContext _db;

    public CollectionRepository(QuotesDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CollectionWithQuotes>> ListByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        // The caller's collections. Items come back with them automatically,
        // because CollectionItem is owned (see QuotesDbContext.OwnsMany) --
        // but an item is only a QuoteId, so the quotes themselves still have
        // to be fetched.
        var collections = await _db.Collections
            .Where(x => x.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Two round trips, not one-per-collection. Every quote id the caller
        // could possibly need is gathered first and fetched in a single
        // query; the per-collection shaping then happens in memory against
        // that lookup. The round trip count is now a constant -- it does not
        // move when the caller has 15 collections instead of 3, which is the
        // whole point of the fix.
        var quoteIds = collections
            .SelectMany(collection => collection.Items)
            .Select(item => item.QuoteId)
            .Distinct()
            .ToList();

        var quotesById = quoteIds.Count == 0
            ? new Dictionary<int, QuoteSummary>()
            : await _db.Quotes
                .Where(quote => quoteIds.Contains(quote.Id))
                .AsNoTracking()
                .Select(quote => new QuoteSummary(quote.Id, quote.Author, quote.Text))
                .ToDictionaryAsync(quote => quote.Id, cancellationToken);

        return collections
            .Select(collection => new CollectionWithQuotes(
                collection.Id,
                collection.Name,
                collection.Items
                    // A missing id means the quote was deleted out from under
                    // the collection. Skip it rather than throwing -- the old
                    // per-collection query silently did the same thing.
                    .Select(item => quotesById.TryGetValue(item.QuoteId, out var quote) ? quote : null)
                    .Where(quote => quote is not null)
                    .Select(quote => quote!)
                    .ToList()))
            .ToList();
    }

    public async Task<Collection?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await _db.Collections
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task AddAsync(
        Collection collection,
        CancellationToken cancellationToken = default)
    {
        await _db.Collections.AddAsync(collection, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        Collection collection,
        CancellationToken cancellationToken = default)
    {
        _db.Collections.Update(collection);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        Collection collection,
        CancellationToken cancellationToken = default)
    {
        _db.Collections.Remove(collection);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
