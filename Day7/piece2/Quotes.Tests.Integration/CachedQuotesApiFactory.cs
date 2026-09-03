using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Caching;
using QuotesApi.Data;
using QuotesApi.Observability;

namespace Quotes.Tests.Integration;

/// <summary>
/// The real app, wired for the Day 21 cache measurements: a FILE-backed SQLite
/// database so concurrent requests are actually concurrent, and the cache
/// switched on (or deliberately off) through the DI container.
///
/// Two things here were learned the hard way on the first run, and both are
/// worth stating because both were wrong in the plan.
///
/// ---------------------------------------------------------------------------
/// 1. WHY A FILE DATABASE AND NOT THE SHARED IN-MEMORY CONNECTION
///
/// QuotesApiFactory keeps ONE SqliteConnection open and hands it to every
/// DbContext, because a SQLite ":memory:" database exists only while a
/// connection to it does. Every test in this project until now was sequential,
/// so one connection was enough.
///
/// The stampede test is the first thing to issue 100 requests at once, and one
/// connection cannot serve concurrent commands: the run produced a wall of
/// HTTP 500s in BOTH the cached and uncached tests -- which briefly looked like
/// a bug in the cache and was a limitation of the harness. A file-backed
/// database gives each DbContext its own pooled connection, so concurrent
/// readers behave like concurrent readers.
///
/// The file is per-factory and deleted on dispose. It is slower than
/// ":memory:", which is why the other test classes keep the shared connection.
///
/// ---------------------------------------------------------------------------
/// 2. WHY THE CACHE IS SWITCHED ON THROUGH DI AND NOT THROUGH CONFIGURATION
///
/// CachingExtensions.AddCaching reads Cache:Enabled at REGISTRATION time, to
/// decide which IQuoteListCache to register. Program.cs calls
/// AddInfrastructure(builder.Configuration) while the builder is still being
/// composed, whereas a WebApplicationFactory's ConfigureAppConfiguration
/// callbacks are applied later, during builder.Build(). So a configuration
/// value added the obvious way never reaches the decision: the first version of
/// this factory set Cache:Enabled=true and the app registered
/// PassThroughQuoteListCache anyway, producing zero hits and a very confusing
/// set of failures.
///
/// ConfigureServices, by contrast, demonstrably runs after the app's own
/// registrations -- it is how this project has always swapped the database and
/// the clock. So the cache is wired here, explicitly.
///
/// THE TRADE THAT MAKES: these tests exercise the cache, not AddCaching's
/// branching. The branching is covered by the default factory, which leaves
/// Cache:Enabled at its false default and therefore gets the pass-through
/// implementation through the real code path.
///
/// (The same timing applies to Day 20's Outbox:RelayEnabled pin in
/// QuotesApiFactory, which is therefore decorative -- the [ModuleInitializer]
/// environment variable is what actually keeps the relay off in tests.)
/// </summary>
public class CachedQuotesApiFactory : QuotesApiFactory
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"quotes-cache-test-{Guid.NewGuid():N}.db");

    private readonly bool _cacheEnabled;

    public CachedQuotesApiFactory(bool cacheEnabled = true) => _cacheEnabled = cacheEnabled;

    /// <summary>Cached pages beyond this bypass the cache. Mirrors CacheOptions.</summary>
    public int MaxCachedPage => 20;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Replace the base factory's shared in-memory connection with a
            // file. Both are SQLite, so EF's additive provider configuration
            // means this second UseSqlite wins -- see the note in
            // QuotesApiFactory about why that is true only within one provider.
            services.RemoveAll<DbContextOptions<QuotesDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<QuotesDbContext>>();

            services.AddDbContext<QuotesDbContext>((serviceProvider, options) => options
                .UseSqlite($"Data Source={_databasePath}")
                .AddInterceptors(
                    serviceProvider.GetRequiredService<DbCommandCounterInterceptor>()));

            services.Configure<CacheOptions>(cache =>
            {
                cache.Enabled = _cacheEnabled;

                // Long enough that nothing expires by accident. A test that
                // passes because an entry happened to survive is a test that
                // fails on a slow machine.
                cache.Expiration = TimeSpan.FromMinutes(10);
                cache.LocalCacheExpiration = TimeSpan.FromMinutes(10);
                cache.GenerationCacheDuration = TimeSpan.Zero;
                cache.MaxCachedPage = MaxCachedPage;
            });

            if (!_cacheEnabled)
                return;

            services.RemoveAll<IQuoteListCache>();
            services.RemoveAll<ICacheGeneration>();

            services.AddSingleton<ICacheGeneration, InMemoryCacheGeneration>();

            // L1 only. Stampede protection is an in-process property, so the
            // headline measurement needs no Redis and no Docker -- which is
            // what keeps this evidence runnable in CI.
            services.AddHybridCache(hybrid =>
            {
                hybrid.MaximumPayloadBytes = 1024 * 1024;
                hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(10),
                    LocalCacheExpiration = TimeSpan.FromMinutes(10)
                };
            });

            services.AddSingleton<IQuoteListCache, HybridQuoteListCache>();
        });
    }

    private void DeleteDatabaseFile()
    {
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* a pooled connection may still be closing; the temp folder is fine to litter */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) DeleteDatabaseFile();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteDatabaseFile();
    }
}
