using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Caching;

/// <summary>
/// Configuration for the read-through cache, bound from the "Cache" section and
/// validated at startup the same way OutboxOptions and ServiceBusOptions are.
///
/// Enabled defaults to FALSE, and that is a lesson rather than a preference.
/// Day 20 shipped Outbox:RelayEnabled defaulting to false because every
/// existing test asserts uncached, unrelayed behaviour -- and then a developer
/// exported Outbox__RelayEnabled=true to watch the relay locally, ran the suite
/// in the same shell, and seven tests failed for reasons that had nothing to do
/// with the change under test. A cache switched on underneath
/// QuoteEndpointTests would do exactly the same, except worse: those tests
/// would pass or fail depending on execution ORDER, since a create in one test
/// would be served from a stale list in another.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Whether the read path consults the cache at all. False registers a
    /// pass-through implementation, so the endpoint code is identical either
    /// way and there is no branch in the request handler.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// L2 (distributed) entry lifetime.
    /// </summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// L1 (in-process) entry lifetime, and deliberately the SHORTER of the two.
    ///
    /// This number is not a performance dial, it is the staleness you are
    /// choosing to accept. An invalidation changes the generation in the shared
    /// store, which every instance picks up within GenerationCacheDuration --
    /// so L1 cannot serve stale data past that, because the key it would serve
    /// is no longer the key being asked for. Left long "because memory is
    /// fast", this would still be bounded, but the entry would sit there
    /// unreachable and unreclaimed for the whole window.
    /// </summary>
    public TimeSpan LocalCacheExpiration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Entries larger than this are not cached. Bounds ONE entry, not total L1
    /// size -- sizing that needs a measurement this exercise does not include.
    /// </summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int MaximumPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// How long the generation counter is memoised in process before being
    /// re-read from the shared store.
    ///
    /// The trade this number expresses: 0 means a shared-store round trip on
    /// every request, which gives back much of what the cache saved. One second
    /// means one round trip per second per instance, and an invalidation
    /// becomes visible to other instances within a second. That is a far
    /// tighter bound than LocalCacheExpiration, which is the whole reason the
    /// generation lives in the key.
    /// </summary>
    public TimeSpan GenerationCacheDuration { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The highest page number that is cached. Beyond it, reads pass straight
    /// through to the database.
    ///
    /// THIS CLOSES A HOLE THE PLAN GOT WRONG. The plan claimed
    /// PaginationOptions.MaxPageSize bounded key cardinality. It bounds `size`
    /// and says nothing about `page`: the endpoint only rejects page &lt; 1, so
    /// ?page=999999 is a valid request that mints a fresh cache key, and a
    /// caller walking the page number would mint them without limit. Each entry
    /// is small, all of them are empty past the end of the data, and none of
    /// them will ever be read again -- unbounded memory spent on nothing, which
    /// is a denial-of-service vector wearing a cache's clothes.
    ///
    /// Bounding the page instead of validating it is the right shape here: a
    /// deep page is a legitimate request that simply should not be cached,
    /// because it is by definition not hot. Cache the pages traffic actually
    /// concentrates on, serve the rest from the database.
    ///
    /// With this, cardinality is at most MaxCachedPage x MaxPageSize -- a
    /// number you can state, which is the whole point.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxCachedPage { get; set; } = 20;

    public RedisOptions Redis { get; set; } = new();

    public sealed class RedisOptions
    {
        /// <summary>
        /// EMPTY MEANS L1 ONLY. It does not mean localhost.
        ///
        /// Day 20's crash proof died on precisely this shape of bug:
        /// ServiceBus:Enabled was true with an empty namespace, and what the
        /// operator got was an ArgumentException from forty frames inside the
        /// Azure SDK naming the SDK's own parameter, because the startup
        /// validator could not run before the client was constructed. So
        /// CachingExtensions checks this eagerly, on the line where the
        /// decision is taken, and names the key to set.
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Prefix for every key this app writes, so a Redis instance shared
        /// with anything else stays legible and a FLUSHDB is never the only
        /// way to clear our data.
        /// </summary>
        public string InstanceName { get; set; } = "quotes:";
    }
}
