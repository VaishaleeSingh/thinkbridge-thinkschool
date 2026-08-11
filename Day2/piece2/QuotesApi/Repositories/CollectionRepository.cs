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
