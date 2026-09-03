using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace QuotesApi.Caching;

/// <summary>
/// The cache's instruments. Registered with OpenTelemetry by name in
/// ObservabilityExtensions -- a meter that is not registered there emits
/// nothing, silently, exactly like an unregistered ActivitySource.
///
/// WHY HIT RATE IS INFERRED RATHER THAN READ:
/// HybridCache does not tell the caller whether a value came from a cache or
/// from the factory. So the truth is taken where it exists -- inside the
/// factory. The factory ran, therefore it was a miss. Everything else was a
/// hit. That inference is exact, which a wrapper guessing from timings would
/// not be.
///
/// WHY THIS CANNOT ANSWER "DID DATABASE LOAD DROP":
/// these counters describe the cache's own behaviour. A high hit rate here is
/// consistent with a database still being hammered by some other path. The
/// database question is answered by DbCommandCounterInterceptor, which counts
/// commands at the point they are executed. Two instruments, because they
/// answer two different questions and one cannot stand in for the other.
/// </summary>
public sealed class CacheMetrics : IDisposable
{
    public const string MeterName = "QuotesApi.Cache";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _invalidations;
    private readonly Histogram<double> _factoryDuration;

    private long _requestCount;
    private long _missCount;
    private long _bypassCount;

    // Bounded on purpose. A hit rate without the number of keys it is over is
    // not a result -- 99% on one key is a warm loop -- so the key count is
    // reported beside it. But tracking keys is itself state, and state that
    // grows with request variety is the bug this cache had to close in the
    // first place, so it stops at the cap and says so.
    private const int DistinctKeyCap = 4096;
    private readonly ConcurrentDictionary<string, byte> _distinctKeys = new();
    private volatile bool _distinctKeysTruncated;

    public CacheMetrics()
    {
        _meter = new Meter(MeterName);

        _requests = _meter.CreateCounter<long>(
            "cache.requests", "requests", "Reads that consulted the cache.");

        _misses = _meter.CreateCounter<long>(
            "cache.misses", "requests", "Reads that had to run the factory, i.e. hit the database.");

        _invalidations = _meter.CreateCounter<long>(
            "cache.invalidations", "operations", "Generation bumps caused by a write.");

        _factoryDuration = _meter.CreateHistogram<double>(
            "cache.factory.duration", "ms", "Time spent in the factory on a miss -- the cost a hit avoids.");

        // Reported next to the count on purpose. A ratio alone is not a
        // result: 99% over a single key is a warm loop, not a cache that
        // works. See /api/cache/stats, which returns both.
        _meter.CreateObservableGauge(
            "cache.hit_ratio", () => HitRatio,
            "ratio", "Hits divided by requests since process start.");
    }

    public long Requests => Interlocked.Read(ref _requestCount);

    public long Misses => Interlocked.Read(ref _missCount);

    public long Hits => Math.Max(0, Requests - Misses);

    /// <summary>Reads that skipped the cache by policy (a page past MaxCachedPage).</summary>
    public long Bypasses => Interlocked.Read(ref _bypassCount);

    public int DistinctKeys => _distinctKeys.Count;

    public bool DistinctKeysTruncated => _distinctKeysTruncated;

    public double HitRatio
    {
        get
        {
            var requests = Requests;
            return requests == 0 ? 0d : (double)Hits / requests;
        }
    }

    public void RecordRequest(string keyFamily)
    {
        Interlocked.Increment(ref _requestCount);
        _requests.Add(1, new KeyValuePair<string, object?>("key_family", keyFamily));
    }

    public void RecordBypass(string keyFamily)
    {
        Interlocked.Increment(ref _bypassCount);
        _requests.Add(1, new KeyValuePair<string, object?>("key_family", keyFamily),
                         new KeyValuePair<string, object?>("outcome", "bypass"));
    }

    public void RecordKey(string key)
    {
        if (_distinctKeys.Count >= DistinctKeyCap)
        {
            _distinctKeysTruncated = true;
            return;
        }

        _distinctKeys.TryAdd(key, 0);
    }

    public void RecordMiss(string keyFamily)
    {
        Interlocked.Increment(ref _missCount);
        _misses.Add(1, new KeyValuePair<string, object?>("key_family", keyFamily));
    }

    public void RecordInvalidation(string keyFamily) =>
        _invalidations.Add(1, new KeyValuePair<string, object?>("key_family", keyFamily));

    public void RecordFactoryDuration(string keyFamily, double milliseconds) =>
        _factoryDuration.Record(milliseconds, new KeyValuePair<string, object?>("key_family", keyFamily));

    public void Dispose() => _meter.Dispose();
}
