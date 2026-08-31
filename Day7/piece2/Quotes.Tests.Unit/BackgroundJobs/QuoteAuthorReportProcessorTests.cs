using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.BackgroundJobs;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Unit.BackgroundJobs;

public class QuoteAuthorReportProcessorTests
{
    [Fact]
    public async Task ProcessAsync_ReturnsTotalsAndRanksTopAuthors()
    {
        await using var db = CreateDbContext();
        db.Quotes.AddRange(
            CreateQuote("Seneca", "One"),
            CreateQuote("Marcus Aurelius", "Two"),
            CreateQuote("Marcus Aurelius", "Three"));
        await db.SaveChangesAsync();

        var processor = new QuoteAuthorReportProcessor(
            db,
            Options.Create(new BackgroundJobQueueOptions
            {
                ProcessingDelaySeconds = 0
            }));

        var result = await processor.ProcessAsync(
            new QuoteAuthorReportJob(Guid.NewGuid(), 1, "test-user"),
            CancellationToken.None);

        result.TotalQuotes.Should().Be(3);
        result.DistinctAuthors.Should().Be(2);
        result.TopAuthors.Should().ContainSingle()
            .Which.Should().Be(new QuoteAuthorCount("Marcus Aurelius", 2));
    }

    private static QuotesDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new QuotesDbContext(options);
    }

    private static Quote CreateQuote(string author, string text) =>
        Quote.Create(author, text, "test-user");
}
