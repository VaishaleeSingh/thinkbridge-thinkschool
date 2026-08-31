using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.BackgroundJobs;
using QuotesApi.Data;

namespace QuotesApi.Services;

public interface IQuoteAuthorReportProcessor
{
    Task<QuoteAuthorReportResult> ProcessAsync(
        QuoteAuthorReportJob job,
        CancellationToken cancellationToken);
}

public sealed class QuoteAuthorReportProcessor(
    QuotesDbContext db,
    IOptions<BackgroundJobQueueOptions> options) : IQuoteAuthorReportProcessor
{
    public async Task<QuoteAuthorReportResult> ProcessAsync(
        QuoteAuthorReportJob job,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(options.Value.ProcessingDelaySeconds);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);

        var totalQuotes = await db.Quotes
            .AsNoTracking()
            .CountAsync(cancellationToken);

        var authorRows = await db.Quotes
            .AsNoTracking()
            .GroupBy(quote => quote.Author)
            .Select(group => new
            {
                Author = group.Key,
                QuoteCount = group.Count()
            })
            .OrderByDescending(author => author.QuoteCount)
            .ThenBy(author => author.Author)
            .Take(job.TopAuthors)
            .ToListAsync(cancellationToken);

        var authors = authorRows
            .Select(author => new QuoteAuthorCount(author.Author, author.QuoteCount))
            .ToList();

        var distinctAuthors = await db.Quotes
            .AsNoTracking()
            .Select(quote => quote.Author)
            .Distinct()
            .CountAsync(cancellationToken);

        return new QuoteAuthorReportResult(totalQuotes, distinctAuthors, authors);
    }
}
