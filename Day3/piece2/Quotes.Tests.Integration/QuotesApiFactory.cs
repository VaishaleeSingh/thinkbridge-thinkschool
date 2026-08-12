using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quotes.Tests.Integration.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

/// <summary>
/// Boots the REAL app (real Program.cs, real DI, real middleware, real
/// authentication/authorization pipeline) with exactly two swaps: the
/// database and the clock. Everything else -- routing, policies, the
/// exception-handling middleware, claims transformation, EF Core itself --
/// runs unmodified, which is the whole point of an integration test over a
/// unit test: it proves the pieces work together, not just in isolation.
///
/// WHY SQLITE ":memory:" INSTEAD OF EF CORE'S INMEMORY PROVIDER:
/// Quotes.Tests.Unit already uses Microsoft.EntityFrameworkCore.InMemory
/// for its narrower, single-class tests -- fine there because it never
/// touches migrations. Here, "EF migrations applied" is one of the things
/// under test, and the InMemory provider doesn't support migrations at
/// all (it just materializes the model directly). A real SQLite database,
/// even an in-memory one, runs the actual migration files, which is the
/// only way to prove they work.
///
/// WHY THE CONNECTION IS OPENED HERE AND KEPT OPEN:
/// SQLite's ":memory:" database only exists for as long as ONE connection
/// to it stays open -- the moment that connection closes, the data (and
/// the whole database) is gone. EF Core's default behavior is to open and
/// close a connection per operation, which would wipe the database
/// between every single query. Passing an already-open SqliteConnection
/// to UseSqlite(...) instead tells EF to reuse this one connection for
/// the DbContext's whole lifetime, keeping the in-memory database alive
/// for as long as the factory itself is alive.
///
/// ISOLATION: every test class creates its OWN QuotesApiFactory instance
/// (see the IAsyncLifetime pattern in each test file) rather than sharing
/// one via IClassFixture. A new factory means a new SqliteConnection,
/// which means a brand new, empty database that only this test's HTTP
/// calls ever touch -- migrations re-run every time, so there is no
/// scenario where one test's data is visible to another.
/// </summary>
public class QuotesApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    /// <summary>
    /// Exposed so tests can advance/inspect "now" (e.g. asserting a
    /// timestamp the app wrote matches this exact instant) without
    /// reaching into the DI container themselves.
    /// </summary>
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureServices(services =>
        {
            // Swap the database: remove the real UseSqlite("Data
            // Source=quotes.db") registration from InfrastructureExtensions
            // and replace it with one bound to our open in-memory
            // connection instead.
            var dbContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<QuotesDbContext>));
            if (dbContextDescriptor is not null)
                services.Remove(dbContextDescriptor);

            services.AddDbContext<QuotesDbContext>(options =>
                options.UseSqlite(_connection));

            // Swap the clock: remove the real SystemClock singleton and
            // replace it with our fixed FakeClock, so any timestamp the
            // app writes during a test (e.g. CollectionItem.AddedAt) is
            // deterministic and assertable.
            var clockDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IClock));
            if (clockDescriptor is not null)
                services.Remove(clockDescriptor);

            services.AddSingleton<IClock>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _connection.Dispose();
    }

    // WebApplicationFactory implements IAsyncDisposable too, and every
    // test in this project disposes its factory with `await
    // factory.DisposeAsync()` (directly, or via IAsyncLifetime). Overriding
    // this explicitly -- rather than relying on the base implementation to
    // eventually call Dispose(bool) -- guarantees the SqliteConnection
    // (and therefore the in-memory database it backs) is actually closed
    // at the end of every test, instead of leaking until GC.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
