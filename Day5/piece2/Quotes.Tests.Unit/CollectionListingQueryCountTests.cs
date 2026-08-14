using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace Quotes.Tests.Unit;

/// <summary>
/// The regression test for the Day 5 N+1.
///
/// Asserting a wall-clock duration would be flaky, and asserting an exact
/// query count would break the moment someone legitimately adds a lookup.
/// What actually has to hold is that the number of database round trips
/// does not grow with the number of collections -- that is the property
/// the N+1 violated, so that is what this pins.
///
/// This uses SQLite rather than the InMemory provider on purpose: InMemory
/// is not a relational provider, so it never issues a DbCommand and a
/// command interceptor would silently count zero.
/// </summary>
public class CollectionListingQueryCountTests
{
    private const string OwnerId = "user-under-test";

    [Fact]
    public async Task ListByOwner_RoundTripCount_DoesNotGrowWithCollectionCount()
    {
        // Arrange
        var few = await CountRoundTripsAsync(collections: 3, quotesPerCollection: 10);
        var many = await CountRoundTripsAsync(collections: 15, quotesPerCollection: 10);

        // Assert
        many.Should().Be(
            few,
            "listing collections must cost a fixed number of round trips -- " +
            "before the fix this went from 4 to 16 because each collection " +
            "fetched its own quotes");
    }

    [Fact]
    public async Task ListByOwner_ReturnsEveryQuoteInEveryCollection()
    {
        // Arrange
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection, interceptor: null);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, collections: 4, quotesPerCollection: 6);

        var repository = new CollectionRepository(db);

        // Act
        var result = await repository.ListByOwnerAsync(OwnerId);

        // Assert -- the fix reshapes the data in memory, so prove the shape
        // survived the reshaping rather than only counting queries.
        result.Should().HaveCount(4);
        result.Should().OnlyContain(collection => collection.Quotes.Count == 6);
        result.SelectMany(collection => collection.Quotes)
            .Should().OnlyContain(quote => !string.IsNullOrWhiteSpace(quote.Text));
    }

    [Fact]
    public async Task ListByOwner_WithNoCollections_DoesNotQueryQuotes()
    {
        // Arrange
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var interceptor = new CommandCountingInterceptor();
        await using var db = CreateContext(connection, interceptor);
        await db.Database.EnsureCreatedAsync();

        var repository = new CollectionRepository(db);
        interceptor.Reset();

        // Act
        var result = await repository.ListByOwnerAsync(OwnerId);

        // Assert -- with nothing to look up, the second query is skipped
        // entirely instead of running a WHERE Id IN () that can never match.
        result.Should().BeEmpty();
        interceptor.Count.Should().Be(1);
    }

    private static async Task<int> CountRoundTripsAsync(int collections, int quotesPerCollection)
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var interceptor = new CommandCountingInterceptor();
        await using var db = CreateContext(connection, interceptor);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, collections, quotesPerCollection);

        var repository = new CollectionRepository(db);

        // Only the read is measured -- EnsureCreated and the seed writes
        // above would otherwise dominate the count.
        interceptor.Reset();
        await repository.ListByOwnerAsync(OwnerId);

        return interceptor.Count;
    }

    private static QuotesDbContext CreateContext(
        SqliteConnection connection,
        CommandCountingInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(connection);

        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        return new QuotesDbContext(builder.Options);
    }

    private static async Task SeedAsync(QuotesDbContext db, int collections, int quotesPerCollection)
    {
        var addedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var c = 0; c < collections; c++)
        {
            var collection = new Collection($"Collection {c + 1}", OwnerId);

            for (var q = 0; q < quotesPerCollection; q++)
            {
                var quote = new Quote
                {
                    Author = $"Author {c}-{q}",
                    Text = $"Quote text {c}-{q}"
                };

                db.Quotes.Add(quote);
                await db.SaveChangesAsync();

                collection.AddItem(quote.Id, addedAt);
            }

            db.Collections.Add(collection);
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
