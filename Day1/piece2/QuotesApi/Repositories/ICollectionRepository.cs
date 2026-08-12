using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface ICollectionRepository
{
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
