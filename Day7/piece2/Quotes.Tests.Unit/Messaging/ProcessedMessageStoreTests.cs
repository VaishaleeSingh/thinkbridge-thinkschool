using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Messaging;

namespace Quotes.Tests.Unit.Messaging;

/// <summary>
/// Tests for <see cref="EfProcessedMessageStore"/>.
///
/// Uses EF Core InMemory provider for the simpler tests (no migrations,
/// faster). Uses a real SQLite in-memory database for the duplicate-insert
/// test because the InMemory provider does NOT enforce unique key constraints
/// — the PK constraint is the actual guarantee, so it must be tested against a
/// real DB engine.
/// </summary>
public class ProcessedMessageStoreTests
{
    private static QuotesDbContext BuildInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new QuotesDbContext(options);
    }

    [Fact]
    public async Task HasSeenAsync_ReturnsFalse_WhenNotRecorded()
    {
        await using var db = BuildInMemoryContext(nameof(HasSeenAsync_ReturnsFalse_WhenNotRecorded));
        var store = new EfProcessedMessageStore(db);

        var seen = await store.HasSeenAsync("msg-001", "audit");
        seen.Should().BeFalse();
    }

    [Fact]
    public async Task HasSeenAsync_ReturnsTrue_AfterRecord()
    {
        await using var db = BuildInMemoryContext(nameof(HasSeenAsync_ReturnsTrue_AfterRecord));
        var store = new EfProcessedMessageStore(db);

        await store.RecordAsync("msg-002", "audit", "Completed");
        var seen = await store.HasSeenAsync("msg-002", "audit");

        seen.Should().BeTrue();
    }

    [Fact]
    public async Task SameMessageId_DifferentSubscriptions_AreDistinct()
    {
        // The composite key is (MessageId, SubscriptionName). Two subscriptions
        // receive the same MessageId from a single publish, representing
        // different pieces of work. A single-column key would suppress one.
        await using var db = BuildInMemoryContext(nameof(SameMessageId_DifferentSubscriptions_AreDistinct));
        var store = new EfProcessedMessageStore(db);

        await store.RecordAsync("msg-003", "audit", "Completed");
        await store.RecordAsync("msg-003", "search-index", "Completed");

        var auditSeen = await store.HasSeenAsync("msg-003", "audit");
        var searchSeen = await store.HasSeenAsync("msg-003", "search-index");
        var otherSeen = await store.HasSeenAsync("msg-003", "notifications");

        auditSeen.Should().BeTrue();
        searchSeen.Should().BeTrue();
        otherSeen.Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ThrowsDbUpdateException_OnDuplicate()
    {
        // Two separate DbContext instances simulate two concurrent processor
        // instances. A single context would throw InvalidOperationException
        // (change-tracker conflict) before the DB constraint fires.
        await using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var opts = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(conn)
            .Options;

        // Apply migrations once on a transient context.
        await using (var migrationDb = new QuotesDbContext(opts))
            await migrationDb.Database.MigrateAsync();

        // First processor succeeds.
        await using (var db1 = new QuotesDbContext(opts))
        {
            var store1 = new EfProcessedMessageStore(db1);
            await store1.RecordAsync("msg-dup", "audit", "Completed");
        }

        // Second processor hits the composite PK constraint.
        await using var db2 = new QuotesDbContext(opts);
        var store2 = new EfProcessedMessageStore(db2);
        Func<Task> second = async () =>
            await store2.RecordAsync("msg-dup", "audit", "Completed");

        await second.Should().ThrowAsync<DbUpdateException>();
    }
}
