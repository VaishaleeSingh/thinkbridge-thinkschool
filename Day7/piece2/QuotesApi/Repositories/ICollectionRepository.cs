using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface ICollectionRepository
{
    // Day 12 -- ListByOwnerAsync used to live here, returning a read-shaped
    // CollectionWithQuotes. It has moved to ICollectionQueries.
    //
    // What is left is a command-side repository: it hands out the Collection
    // AGGREGATE and persists it. Every method below either loads the real
    // entity (so its invariants are reachable) or saves one. Nothing here
    // returns a projection, which is what keeps this interface from drifting
    // into a general-purpose query surface.

    /// <summary>
    /// The tracked Collection aggregate, for a caller that intends to change
    /// it. Tracked on purpose: SaveChanges needs the change tracker to see
    /// what the aggregate's own methods mutated.
    /// </summary>
    Task<Collection?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Collection collection,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Collection collection,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Collection collection,
        CancellationToken cancellationToken = default);
}
