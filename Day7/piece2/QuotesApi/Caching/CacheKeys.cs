namespace QuotesApi.Caching;

/// <summary>
/// Cache key construction, in one place.
///
/// Shape, as it appears in Redis: quotes:list:v1:g{generation}:p{page}:s{size}
///
/// The leading "quotes:" comes from CacheOptions.Redis.InstanceName, which the
/// distributed cache prepends to every key it writes -- NOT from here. The keys
/// built below start at "list:".
///
/// This was the other way round until the Redis suite looked at an actual key
/// and found "quotes:quotes:list:v1:g0:p1:s20": both this class and
/// InstanceName were namespacing, one job done twice, and the shape documented
/// everywhere was wrong by one segment. InstanceName is the better place for it
/// -- it also covers the generation key, and anything else the framework writes
/// on our behalf, without each key having to remember.
///
/// Three parts, each carrying its weight:
///
///   v1          the QuoteListPage contract version. Bumped when the DTO
///               changes shape, so a new build cannot read an old entry.
///
///   g{gen}      the invalidation mechanism. A write bumps the generation, so
///               every previously written key becomes unaddressable at once
///               and expires on its own. See ICacheGeneration for why this was
///               chosen over HybridCache's tag-based removal.
///
///   p / s       the only two request inputs. Bounded by
///               PaginationOptions.MaxPageSize, which the endpoint validates
///               BEFORE a key is built -- see the note on cardinality below.
///
/// WHY KEY CARDINALITY IS A SECURITY CONCERN AND NOT A TIDINESS ONE:
/// if `size` reached this method unvalidated, a caller iterating size=1..N
/// would mint N cache entries for the same underlying data, each held in
/// memory. That is memory exhaustion dressed as a cache. The endpoint's
/// existing page/size validation therefore has to run first, and
/// CacheKeysTests asserts the ordering by asserting the endpoint rejects an
/// oversized page before any entry exists.
/// </summary>
public static class CacheKeys
{
    /// <summary>
    /// The QuoteListPage contract version. Bump on any shape change to
    /// QuoteListPage or QuoteListItem.
    /// </summary>
    public const string Version = "v1";

    /// <summary>
    /// Identifies this key family in metrics and in the DB-command counter, so
    /// "the quotes list did N database round trips" is a statement about one
    /// query and not about all traffic.
    /// </summary>
    public const string QuoteListFamily = "quotes.list";

    /// <summary>
    /// The EF query tag applied in QuoteRepository.GetPagedAsync. The
    /// interceptor matches on this rather than on SQL text: the SQL changes
    /// whenever the query does, the tag only changes when someone means it to.
    /// </summary>
    public const string QuoteListQueryTag = "quotes-list";

    public static string QuoteList(string generation, int page, int size) =>
        $"list:{Version}:g{generation}:p{page}:s{size}";

    /// <summary>
    /// The key holding the generation token in the distributed cache.
    ///
    /// A TOKEN, NOT A COUNTER, and that is what lets this work through
    /// IDistributedCache alone with no Redis-specific dependency: invalidation
    /// only requires the value to CHANGE, never to increase. Two writers
    /// bumping concurrently both write a new token; whichever lands, the token
    /// differs from the one readers held, so the old keys are unreachable
    /// either way. Needing atomic INCR would have meant taking a direct
    /// StackExchange.Redis dependency for no additional correctness.
    /// </summary>
    public const string GenerationKey = $"list:{Version}:generation";

    /// <summary>
    /// The token used when none has been written yet.
    ///
    /// Fixed, deliberately. If a missing token caused each instance to mint its
    /// own, every instance would address a different key on a cold cache and
    /// none of them would ever hit each other's entries -- a shared cache that
    /// silently behaves like N private ones.
    /// </summary>
    public const string InitialGeneration = "0";
}
