using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using QuotesApi.Caching;
using QuotesApi.Observability;
using StackExchange.Redis;

namespace QuotesApi.Extensions;

/// <summary>
/// Wires the read-through cache. Kept separate from InfrastructureExtensions
/// for the same reason MessagingExtensions and OutboxExtensions are: it is one
/// switchable concern with its own failure modes.
///
/// THE INSTRUMENTS ARE REGISTERED UNCONDITIONALLY, the cache is not. That
/// asymmetry is deliberate: the whole exercise is a before/after measurement,
/// and the "before" is the run with Cache:Enabled=false. If the counters only
/// existed when caching was on, the baseline could not be measured with the
/// same instrument as the result, and comparing two different instruments is
/// not a comparison.
/// </summary>
public static class CachingExtensions
{
    public static IServiceCollection AddCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Always on. See the class note.
        services.AddSingleton<CacheMetrics>();
        services.AddSingleton<DbCommandCounterInterceptor>();

        var options = configuration
            .GetSection(CacheOptions.SectionName)
            .Get<CacheOptions>() ?? new CacheOptions();

        if (!options.Enabled)
        {
            // Pass-through, so the endpoint has no branch and the uncached path
            // is the same code shape as the cached one.
            services.AddSingleton<ICacheGeneration, InMemoryCacheGeneration>();
            services.AddSingleton<IQuoteListCache, PassThroughQuoteListCache>();
            return services;
        }

        var redisConnectionString = options.Redis.ConnectionString;
        var useRedis = !string.IsNullOrWhiteSpace(redisConnectionString);

        if (useRedis)
        {
            // FAIL FAST, HERE, ON A MALFORMED CONNECTION STRING.
            //
            // Day 20 taught this the expensive way. ServiceBus:Enabled=true
            // with an empty namespace produced, at startup,
            //
            //   ArgumentException: The value '' is not a well-formed Service Bus
            //   fully qualified namespace. (Parameter 'fullyQualifiedNamespace')
            //
            // from forty frames inside the Azure SDK -- a stack that names the
            // SDK's parameter and never mentions the setting to change, because
            // the hosted-service factory resolved the client before the startup
            // validator could run. Parsing here, on the line where the decision
            // is taken, cannot be outrun by anything.
            //
            // Note an EMPTY string is not an error: it means L1 only. Only a
            // string that is present and unparseable is.
            try
            {
                _ = ConfigurationOptions.Parse(redisConnectionString!);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Cache:Redis:ConnectionString is set but could not be parsed: {exception.Message} "
                    + "Set it to a valid Redis endpoint (e.g. \"localhost:6379\"), or clear it to run "
                    + "with the in-memory layer only. The environment-variable spelling uses double "
                    + "underscores: Cache__Redis__ConnectionString.",
                    exception);
            }

            services.AddStackExchangeRedisCache(redis =>
            {
                var configurationOptions = ConfigurationOptions.Parse(redisConnectionString!);

                // A Redis that is down at startup must not stop the app from
                // starting. With AbortOnConnectFail true (the library default
                // for some paths) a cold Redis becomes a failed deployment,
                // which turns an optional cache into a hard dependency.
                configurationOptions.AbortOnConnectFail = false;

                redis.ConfigurationOptions = configurationOptions;
                redis.InstanceName = options.Redis.InstanceName;
            });

            // The token has to be shared, or each instance addresses its own
            // key space and the "distributed" cache behaves like N private ones.
            services.AddSingleton<ICacheGeneration, DistributedCacheGeneration>();
        }
        else
        {
            // L1 only. Correct for a single instance, and enough to demonstrate
            // stampede protection -- deduplication is an in-process property
            // and needs no distributed layer at all.
            services.AddSingleton<ICacheGeneration, InMemoryCacheGeneration>();
        }

        // AddHybridCache uses whatever IDistributedCache is registered as its
        // L2 and falls back to L1 only when none is. So the Redis branch above
        // is the ONLY place that decides one level or two -- there is no second
        // switch to keep in step with it.
        services.AddHybridCache(hybrid =>
        {
            hybrid.MaximumPayloadBytes = options.MaximumPayloadBytes;

            hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = options.Expiration,
                LocalCacheExpiration = options.LocalCacheExpiration
            };
        });

        services.AddSingleton<IQuoteListCache, HybridQuoteListCache>();

        return services;
    }

    /// <summary>
    /// GET /api/cache/stats -- hit rate, the key count it is over, and the
    /// database command counts.
    ///
    /// Mapped in every environment, not behind the Development-only
    /// diagnostics guard, for the same reason Day 20's outbox status endpoint
    /// is: this is what an operator reads when they suspect the cache has
    /// stopped helping, and that suspicion does not arise in Development.
    ///
    /// It reports hits, requests, distinct keys and DB commands together
    /// because any one of them alone is misleading. A high hit rate over one
    /// key is a warm loop. A low DB count with a low request count is an idle
    /// process. The numbers only mean something as a set.
    /// </summary>
    public static IEndpointRouteBuilder MapCacheEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cache/stats", (
            CacheMetrics metrics,
            DbCommandCounterInterceptor dbCommands,
            IOptions<CacheOptions> options) =>
        {
            var cacheOptions = options.Value;

            return Results.Ok(new
            {
                enabled = cacheOptions.Enabled,

                // Whether, not what. A connection string in a diagnostics
                // response is a credential in a log.
                redisConfigured = !string.IsNullOrWhiteSpace(cacheOptions.Redis.ConnectionString),

                expiration = cacheOptions.Expiration,
                localCacheExpiration = cacheOptions.LocalCacheExpiration,
                maxCachedPage = cacheOptions.MaxCachedPage,

                requests = metrics.Requests,
                hits = metrics.Hits,
                misses = metrics.Misses,
                bypasses = metrics.Bypasses,
                hitRatio = Math.Round(metrics.HitRatio, 4),

                // Read this next to hitRatio, always.
                distinctKeys = metrics.DistinctKeys,
                distinctKeysTruncated = metrics.DistinctKeysTruncated,

                // The number the exercise actually asks about. Cache-side
                // counters cannot establish it.
                dbCommands = dbCommands.Snapshot()
            });
        }).RequireAuthorization();

        return app;
    }
}
