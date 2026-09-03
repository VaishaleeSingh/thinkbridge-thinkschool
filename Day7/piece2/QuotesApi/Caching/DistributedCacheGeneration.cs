using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using QuotesApi.Services;

namespace QuotesApi.Caching;

/// <summary>
/// Generation token held in the distributed cache, so every instance agrees on
/// which keys are current.
///
/// Depends on IDistributedCache, not on StackExchange.Redis. See
/// CacheKeys.GenerationKey for why a token rather than an atomic counter is
/// enough, and therefore why no Redis-specific API is needed.
///
/// MEMOISED, because the alternative is absurd: reading the token from Redis on
/// every request would add a network round trip to every cache hit, which is
/// most of what the cache was saving. One read per GenerationCacheDuration per
/// instance (one second by default) bounds both the cost and the staleness.
///
/// FAILURE POSTURE: if the distributed cache is unreachable, this returns the
/// last token it saw, falling back to the initial token if it has never seen
/// one. It does NOT throw. A cache being unavailable must degrade a request to
/// a database read, never fail it -- and a generation lookup that threw would
/// turn a Redis outage into a 500 on every list request, which is worse than
/// having no cache at all.
/// </summary>
public sealed class DistributedCacheGeneration(
    IDistributedCache distributedCache,
    IClock clock,
    IOptions<CacheOptions> options,
    ILogger<DistributedCacheGeneration> logger) : ICacheGeneration
{
    private readonly CacheOptions _options = options.Value;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private string _cached = CacheKeys.InitialGeneration;
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    public async ValueTask<string> GetAsync(CancellationToken cancellationToken = default)
    {
        if (clock.UtcNow - _refreshedAt < _options.GenerationCacheDuration)
            return _cached;

        // One refresh at a time. Without the gate, a burst arriving just after
        // the memo expires would every one of them go to Redis -- a stampede
        // on the anti-stampede mechanism.
        if (!await _refreshGate.WaitAsync(TimeSpan.Zero, cancellationToken))
            return _cached;

        try
        {
            if (clock.UtcNow - _refreshedAt < _options.GenerationCacheDuration)
                return _cached;

            var stored = await distributedCache.GetStringAsync(CacheKeys.GenerationKey, cancellationToken);

            _cached = string.IsNullOrEmpty(stored) ? CacheKeys.InitialGeneration : stored;
            _refreshedAt = clock.UtcNow;
        }
        catch (Exception exception)
        {
            // Deliberately swallowed. See the failure-posture note above.
            // Logged at Warning rather than Error because the request still
            // succeeds -- it just may serve a slightly stale generation.
            logger.LogWarning(
                exception,
                "Could not read the cache generation token. Continuing with {Generation}.",
                _cached);

            // Push the next attempt out by the memo window so a hard-down
            // Redis is not retried on every single request.
            _refreshedAt = clock.UtcNow;
        }
        finally
        {
            _refreshGate.Release();
        }

        return _cached;
    }

    public async ValueTask BumpAsync(CancellationToken cancellationToken = default)
    {
        // Monotonic and unique: ticks make it ordered for a human reading keys
        // in redis-cli, the suffix makes two bumps in the same tick distinct.
        var token = $"{clock.UtcNow.UtcTicks}-{Guid.NewGuid():N}"[..24];

        try
        {
            await distributedCache.SetStringAsync(
                CacheKeys.GenerationKey,
                token,
                new DistributedCacheEntryOptions(),   // no expiry: the token IS the state
                cancellationToken);

            _cached = token;
            _refreshedAt = clock.UtcNow;
        }
        catch (Exception exception)
        {
            // A failed bump is a correctness problem, not a performance one:
            // readers keep the old token and keep serving stale pages until
            // natural expiry. Error, not Warning, and the local token is still
            // advanced so at least THIS instance stops serving the old data.
            logger.LogError(
                exception,
                "Could not write the cache generation token. Other instances may serve stale "
                + "quote lists for up to the entry expiration.");

            _cached = token;
            _refreshedAt = clock.UtcNow;
        }
    }
}
