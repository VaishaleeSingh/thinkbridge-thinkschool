using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface ICollectionRepository
{
    /// <summary>
    /// Every collection owned by the caller, with the quotes it contains.
    /// </summary>
    Task<IReadOnlyList<CollectionWithQuotes>> ListByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default);

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
