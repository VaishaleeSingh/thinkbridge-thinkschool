using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;

namespace Quotes.Tests.Unit.Messaging.Outbox;

/// <summary>
/// A real SQLite database and a real DI scope factory, for testing the relay.
///
/// SQLITE, NOT THE INMEMORY PROVIDER, and not as a preference. The relay does
/// all four of its state changes -- claim, mark sent, release, park -- with
/// ExecuteUpdateAsync, which the InMemory provider does not implement at all.
/// A test written against InMemory would not be a weaker test of the claim; it
/// would throw on the first line of it.
///
/// It is also the only way the claim's guarantee can be tested. Claiming is a
/// conditional UPDATE checked for rows-affected, and "how many rows did that
/// UPDATE change" is a question only a database engine answers.
/// </summary>
internal sealed class OutboxTestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<OutboxMetrics> _metrics = new();

    public FakeClock Clock { get; }

    private OutboxTestHost(SqliteConnection connection, FakeClock clock)
    {
        _connection = connection;
        Clock = clock;
    }

    public static async Task<OutboxTestHost> CreateAsync()
    {
        // Kept open for the host's lifetime: a SQLite ":memory:" database
        // exists only while a connection to it does.
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var host = new OutboxTestHost(connection, new FakeClock(DateTimeOffset.UtcNow));

        await using var db = host.NewContext();
        await db.Database.EnsureCreatedAsync();

        return host;
    }

    public QuotesDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new QuotesDbContext(options);
    }

    /// <summary>
    /// Builds a relay with its own DI container -- and therefore its own
    /// LockOwner -- over the SAME database. Two relays built from one host are
    /// two competing consumers of one outbox, which is the arrangement the
    /// claim exists for.
    /// </summary>
    public OutboxRelayService BuildRelay(
        IQuoteEventPublisher publisher,
        OutboxOptions? options = null)
    {
        var services = new ServiceCollection();

        services.AddDbContext<QuotesDbContext>(builder => builder.UseSqlite(_connection));
        services.AddSingleton(publisher);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var metrics = new OutboxMetrics();
        _metrics.Add(metrics);

        return new OutboxRelayService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ChannelOutboxSignal(),
            Options.Create(options ?? new OutboxOptions { BatchSize = 10, MaxAttempts = 3 }),
            metrics,
            Clock,
            NullLogger<OutboxRelayService>.Instance);
    }

    /// <summary>
    /// Enqueues one row through the real writer and commits it, which is what
    /// a committed domain transaction leaves behind.
    /// </summary>
    public async Task<long> EnqueueAsync(int quoteId, string eventType = "QuoteCreated")
    {
        await using var db = NewContext();
        var writer = new EfOutboxWriter(db);

        var evt = eventType switch
        {
            "QuoteDeleted" => QuoteChangedEvent.Deleted(quoteId, "owner-1", Clock.UtcNow),
            "QuoteUpdated" => QuoteChangedEvent.Updated(quoteId, "owner-1", "Author", "Text", Clock.UtcNow),
            _ => QuoteChangedEvent.Created(quoteId, "owner-1", "Author", "Text", Clock.UtcNow)
        };

        var row = writer.Enqueue(evt);
        await db.SaveChangesAsync();

        return row.Id;
    }

    public async Task<OutboxMessage> GetRowAsync(long id)
    {
        await using var db = NewContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();

        foreach (var metric in _metrics)
            metric.Dispose();

        await _connection.DisposeAsync();
    }
}
