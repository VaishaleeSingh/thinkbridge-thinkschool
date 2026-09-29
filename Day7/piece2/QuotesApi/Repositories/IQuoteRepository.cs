using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IQuoteRepository
{
    Task<(IReadOnlyList<Quote> Items, int Total)> GetPagedAsync(
        int page,
        int size,
        string? author,
        CancellationToken cancellationToken);

    Task<Quote?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken);

    /// <summary>
    /// True if a quote with exactly this author and text already exists.
    /// Pass <paramref name="excludeId"/> on update so a quote does not count
    /// as a duplicate of itself.
    /// </summary>
    Task<bool> ExistsAsync(
        string author,
        string text,
        int? excludeId,
        CancellationToken cancellationToken);

    Task<Quote> AddAsync(
        Quote quote,
        CancellationToken cancellationToken);

    Task<Quote?> UpdateAsync(
        int id,
        string author,
        string text,
        string backgroundImageUrl,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken);
}