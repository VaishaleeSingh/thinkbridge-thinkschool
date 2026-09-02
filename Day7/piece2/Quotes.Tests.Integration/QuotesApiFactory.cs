using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    ///
    /// DELIBERATELY seeded from the REAL wall clock (DateTimeOffset.UtcNow)
    /// rather than a fixed, arbitrary date -- this is fixed for the
    /// lifetime of one factory (every timestamp the app writes during a
    /// single test still comes from this one frozen instant, which is
    /// all the "fake clock" guarantee actually requires), but a
    /// hardcoded date like 2026-06-01 will eventually sit in the PAST
    /// relative to whenever the tests actually run.
    ///
    /// That's not hypothetical -- it happened on the first real test run
    /// of this project. Two things check token validity against the REAL
    /// system clock no matter what IClock the app is wired to:
    /// ASP.NET Core's JWT bearer ValidateLifetime, and
    /// RefreshToken.IsExpired (DateTime.UtcNow > ExpiresAt). Both are
    /// outside this app's IClock abstraction entirely. Mint an access or
    /// refresh token using a FakeClock "now" that's already in the past
    /// by real-clock time, and every one of them comes back expired on
    /// its very first use -- every authenticated request in the suite
    /// failed with 401 for exactly this reason. Anchoring to real UtcNow
    /// keeps the fake clock deterministic within a test while staying
    /// permanently ahead of the wall clock that JWT validation actually
    /// checks against.
    /// </summary>
    public FakeClock Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureAppConfiguration(configuration =>
        {
            // Day 20 -- FORCE THE RELAY OFF, whatever the environment says.
            //
            // Added as an in-memory configuration source, so it sits ABOVE
            // environment variables in the precedence chain and cannot be
            // overridden by them. That is the whole point: a developer who has
            // exported Outbox__RelayEnabled=true to watch the relay work
            // locally will, in the same shell, run the tests -- and a test
            // process inherits its parent's environment. The relay then starts
            // inside every test host, drains the outbox before the assertions
            // read it, and (here) hammers the one shared in-memory SQLite
            // connection concurrently, which fails as "not an error" and
            // "unable to delete/modify user-function due to active statements"
            // in tests that have nothing to do with messaging.
            //
            // Every outbox test in this project asserts on rows the relay
            // would consume. Leaving that switch to ambient state means those
            // assertions are only valid when nobody happens to have exported
            // the variable -- which is not a test, it is a coincidence.
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:RelayEnabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Swap the database: remove the real UseSqlite("Data
            // Source=quotes.db") registration from InfrastructureExtensions
            // and replace it with one bound to our open in-memory
            // connection instead.
            //
            // Removing DbContextOptions<QuotesDbContext> alone happened to
            // be enough here, but only because both the real registration
            // and this one call UseSqlite -- since EF Core 5,
            // AddDbContext<T>(...) registers its configuration lambda as
            // an additive IDbContextOptionsConfiguration<T> entry rather
            // than replacing a prior one, so the ORIGINAL UseSqlite(...)
            // call still runs too; it just happens to get silently
            // overwritten by this second UseSqlite(...) call targeting the
            // same provider extension slot. Swap the provider entirely
            // (as Quotes.Tests.Integration.SqlServer's factory does) and
            // that silent overwrite becomes a hard EF Core error instead
            // ("services for database providers X, Y have been
            // registered") -- discovered building that project. Removing
            // both descriptor types here too costs nothing and keeps this
            // factory correct if it's ever pointed at a different provider.
            services.RemoveAll<DbContextOptions<QuotesDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<QuotesDbContext>>();

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
