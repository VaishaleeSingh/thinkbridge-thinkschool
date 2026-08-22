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

    // Day 12 -- the ListByOwnerAsync implementation that used to sit here has
    // moved to Queries/CollectionQueries.cs.
    //
    // Worth recording what it did, because it is the anti-pattern this split
    // exists to remove: it loaded every Collection aggregate for the owner
    // (with all of their owned items), gathered every quote id across all of
    // them, ran a SECOND query for those quotes -- selecting Author and the
    // full Text -- and then reshaped the result in memory. Two round trips,
    // full quote bodies fetched for a list screen that renders none of them,
    // and aggregates materialized only to be projected away.
    //
    // None of that was a bug. It was a read being served by a type whose job
    // is writes, and it is the predictable shape that takes.

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
