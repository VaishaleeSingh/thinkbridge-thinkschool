using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Caching;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Observability;
using QuotesApi.Services;
using StackExchange.Redis;

namespace Quotes.Tests.Integration.Redis;

/// <summary>
/// One instance of the real app, with the cache on, L2 pointed at the shared
/// Redis container, and its OWN SQLite file.
///
/// THE SEPARATE DATABASE IS THE POINT, not an accident of isolation. Two hosts
/// sharing one database could not prove anything about L2: a "hit" on the
/// second host would be indistinguishable from it simply reading the same rows.
/// Give each host its own data, make that data differ, and a second host
/// returning the FIRST host's payload can only mean the entry came from Redis.
///
/// The cache is wired here in ConfigureServices rather than through
/// configuration, for the reason Day 21 discovered the hard way:
/// CachingExtensions.AddCaching reads Cache:Enabled at REGISTRATION time, while
/// a WebApplicationFactory's ConfigureAppConfiguration callbacks are applied
/// later during builder.Build(). A configuration value set the obvious way
/// never reaches the decision.
/// </summary>
public sealed class RedisCachedQuotesApiFactory(string redisConnectionString)
    : WebApplicationFactory<Program>
{
    public const string KeyPrefix = "quotes:";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"quotes-redis-l2-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<QuotesDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<QuotesDbContext>>();

            services.AddDbContext<QuotesDbContext>((serviceProvider, options) => options
                .UseSqlite($"Data Source={_databasePath}")
                .AddInterceptors(
                    serviceProvider.GetRequiredService<DbCommandCounterInterceptor>()));

            services.Configure<CacheOptions>(cache =>
            {
                cache.Enabled = true;
                cache.Expiration = TimeSpan.FromMinutes(10);

                // SHORT, and deliberately so. With a ten minute L1 a second
                // read on the same host would be served from memory and this
                // suite could never observe L2 at all -- every assertion about
                // Redis would pass for the wrong reason. Half a second is long
                // enough to be a cache and short enough to step out of the way.
                cache.LocalCacheExpiration = TimeSpan.FromMilliseconds(500);

                cache.GenerationCacheDuration = TimeSpan.Zero;
                cache.MaxCachedPage = 20;
                cache.Redis.ConnectionString = redisConnectionString;
                cache.Redis.InstanceName = KeyPrefix;
            });

            services.RemoveAll<IQuoteListCache>();
            services.RemoveAll<ICacheGeneration>();

            var redis = ConfigurationOptions.Parse(redisConnectionString);
            redis.AbortOnConnectFail = false;

            services.AddStackExchangeRedisCache(options =>
            {
                options.ConfigurationOptions = redis;
                options.InstanceName = KeyPrefix;
            });

            // The distributed generation, so invalidation crosses instances.
            // With InMemoryCacheGeneration each host would keep its own token,
            // address a different key space, and the shared cache would behave
            // like N private ones -- the failure this suite is here to rule out.
            services.AddSingleton<ICacheGeneration, DistributedCacheGeneration>();

            services.AddHybridCache(hybrid =>
            {
                hybrid.MaximumPayloadBytes = 1024 * 1024;
                hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(10),
                    LocalCacheExpiration = TimeSpan.FromMilliseconds(500)
                };
            });

            services.AddSingleton<IQuoteListCache, HybridQuoteListCache>();
        });
    }

    /// <summary>
    /// Mints a token directly through IAuthService, the same shortcut the other
    /// integration suites use -- it exercises the real token-validation path
    /// without an HTTP round trip through /api/auth.
    /// </summary>
    public async Task<string> IssueTokenAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var user = new User
        {
            Email = $"redis-{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused",
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return authService.GenerateAccessToken(user);
    }

    /// <summary>
    /// Inserts a quote straight into this host's database, bypassing the API.
    ///
    /// Bypassing it is the whole trick: going through POST /api/quotes would
    /// bump the generation and invalidate the very entry the test is about to
    /// check. This makes the two hosts' underlying data differ WITHOUT telling
    /// the cache anything.
    /// </summary>
    public async Task<int> AddQuoteDirectlyAsync(string author, string text)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var quote = Quote.Create(author, text, createdByUserId: null);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();

        return quote.Id;
    }

    public T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    private void DeleteDatabaseFile()
    {
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
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
