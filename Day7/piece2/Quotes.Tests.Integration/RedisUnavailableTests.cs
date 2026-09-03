using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Caching;
using QuotesApi.Data;
using QuotesApi.Observability;
using StackExchange.Redis;

namespace Quotes.Tests.Integration;

/// <summary>
/// The cache is configured with a Redis endpoint that nothing is listening on.
///
/// This is the failure mode that decides whether the cache is an optimisation
/// or a new single point of failure. A distributed cache that can fail the
/// request is worse than no cache at all: it converts a Redis outage into an
/// outage of the API, for a component whose entire job is to be optional.
///
/// NEEDS NO DOCKER, deliberately. Pointing at a closed port is a better test of
/// this than stopping a container would be -- it is deterministic, it runs in
/// CI, and "unreachable" is exactly the state being asserted. A test that
/// requires infrastructure to prove the app survives losing that
/// infrastructure tends not to get run.
/// </summary>
public class RedisUnavailableTests : IAsyncLifetime
{
    private UnreachableRedisFactory _factory = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _factory = new UnreachableRedisFactory();
        _client = _factory.CreateClient();
        _token = await OutboxAtomicityTests.IssueTokenAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private HttpRequestMessage Authed(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    [Fact]
    public async Task Reads_still_succeed_when_redis_is_unreachable()
    {
        using var response = await _client.SendAsync(Authed("/api/quotes?page=1&size=20"));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a cache being unavailable must degrade the request to a database read, never fail it");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"items\"");
    }

    [Fact]
    public async Task The_local_layer_still_serves_hits_with_no_distributed_layer()
    {
        // L1 does not depend on L2 being reachable, so stampede protection and
        // in-process hits survive a Redis outage. This is the property that
        // makes the degradation graceful rather than merely non-fatal: the
        // database sees one read per key per instance, not one per request.
        var dbCommands = _factory.Services.GetRequiredService<DbCommandCounterInterceptor>();
        dbCommands.Reset();

        using var first = await _client.SendAsync(Authed("/api/quotes?page=1&size=20"));
        using var second = await _client.SendAsync(Authed("/api/quotes?page=1&size=20"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        dbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(
            2, "the first read loaded, the second was served from L1 despite Redis being down");
    }

    [Fact]
    public async Task A_write_still_succeeds_when_the_generation_cannot_be_written()
    {
        // The generation token lives in the distributed cache, so with Redis
        // down the bump cannot be persisted. DistributedCacheGeneration logs
        // that at Error and advances its local token anyway -- because the
        // alternative is a write path that fails because a CACHE is down,
        // which would make the cache a hard dependency of every mutation.
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                author = "Redis Down",
                text = "A write must not depend on a cache being reachable."
            })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var response = await _client.SendAsync(create);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// The real app with the cache on and Redis pointed at a closed port.
    ///
    /// The timeouts are aggressive on purpose. StackExchange.Redis defaults to
    /// a 5-second connect timeout with retries, which would make every test
    /// here take tens of seconds and teach a reader that graceful degradation
    /// is slow. It is not: the point is that the failure is bounded and the
    /// request still completes.
    /// </summary>
    private sealed class UnreachableRedisFactory : QuotesApiFactory
    {
        // Port 6399: outside the usual Redis default, and nothing in this
        // repository binds it.
        private const string ClosedEndpoint = "127.0.0.1:6399";

        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"quotes-redis-down-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

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
                    cache.LocalCacheExpiration = TimeSpan.FromMinutes(10);
                    cache.GenerationCacheDuration = TimeSpan.Zero;
                    cache.MaxCachedPage = 20;
                    cache.Redis.ConnectionString = ClosedEndpoint;
                });

                services.RemoveAll<IQuoteListCache>();
                services.RemoveAll<ICacheGeneration>();

                var redis = ConfigurationOptions.Parse(ClosedEndpoint);

                // Without this the host would refuse to start because a cache
                // is down -- the exact coupling this test exists to disprove.
                redis.AbortOnConnectFail = false;
                redis.ConnectTimeout = 200;
                redis.ConnectRetry = 1;
                redis.SyncTimeout = 200;
                redis.AsyncTimeout = 200;

                services.AddStackExchangeRedisCache(options =>
                {
                    options.ConfigurationOptions = redis;
                    options.InstanceName = "quotes-down:";
                });

                // The distributed generation, deliberately -- so the failure
                // path in DistributedCacheGeneration is the one under test
                // rather than the in-memory implementation that cannot fail.
                services.AddSingleton<ICacheGeneration, DistributedCacheGeneration>();

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
}
