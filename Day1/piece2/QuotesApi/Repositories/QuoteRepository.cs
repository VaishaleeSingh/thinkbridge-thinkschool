using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class QuoteRepository : IQuoteRepository
{
    private readonly QuotesDbContext _db;
    private readonly ILogger<QuoteRepository> _logger;

    public QuoteRepository(
        QuotesDbContext db,
        ILogger<QuoteRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<Quote> Items, int Total)> GetPagedAsync(
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var query = _db.Quotes.AsNoTracking();

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(q => q.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        _logger.LogInformation(
            "Retrieved {Count} quotes for page {Page}",
            items.Count,
            page);

        return (items, total);
    }

    public async Task<Quote?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await _db.Quotes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);
    }

    public async Task<Quote> AddAsync(
        Quote quote,
        CancellationToken cancellationToken)
    {
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created quote {QuoteId}",
            quote.Id);

        return quote;
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (quote is null)
            return false;

        _db.Quotes.Remove(quote);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Deleted quote {QuoteId}",
            id);

        return true;
    }
}