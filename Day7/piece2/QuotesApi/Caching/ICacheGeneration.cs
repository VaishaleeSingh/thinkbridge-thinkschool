namespace QuotesApi.Caching;

/// <summary>
/// The invalidation mechanism: a token embedded in every cache key. Bumping it
/// makes every previously written key unaddressable at once, and those entries
/// then expire on their own.
///
/// WHY THIS RATHER THAN HybridCache's RemoveByTagAsync:
///
/// Tag-based removal can reach the distributed layer, but it cannot reach into
/// another instance's L1 memory. So with tags, a write would clear Redis and
/// every other replica would keep serving its own in-process copy until
/// LocalCacheExpiration elapsed -- 30 seconds of stale reads on a list that
/// just changed, with nothing to indicate it.
///
/// Because the token is part of the KEY, a bump invalidates L1 too: the entry
/// is still sitting in memory, but nothing asks for it any more. Other
/// instances notice within GenerationCacheDuration, which is one second by
/// default rather than thirty. Strictly better, and it needs no support from
/// the cache implementation at all.
///
/// The secondary reason is that RemoveByTagAsync's API surface has existed
/// longer than a working default implementation of it, and a tag removal that
/// silently does nothing is the worst failure available here: writes would
/// appear to invalidate, reads would stay stale, and nothing would log.
/// </summary>
public interface ICacheGeneration
{
    /// <summary>The current token. Cheap: memoised in process.</summary>
    ValueTask<string> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the token. Called AFTER a write commits -- see QuoteWriteService.
    /// Bumping inside the transaction would invalidate for a write that then
    /// rolled back, throwing away a valid cache for nothing.
    /// </summary>
    ValueTask BumpAsync(CancellationToken cancellationToken = default);
}
