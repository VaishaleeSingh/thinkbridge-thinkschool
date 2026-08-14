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

        var result = new List<CollectionWithQuotes>(collections.Count);

        foreach (var collection in collections)
        {
            var quoteIds = collection.Items.Select(item => item.QuoteId).ToList();

            var quotes = await _db.Quotes
                .Where(quote => quoteIds.Contains(quote.Id))
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            result.Add(new CollectionWithQuotes(
                collection.Id,
                collection.Name,
                quotes
                    .Select(quote => new QuoteSummary(quote.Id, quote.Author, quote.Text))
                    .ToList()));
        }

        return result;
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
