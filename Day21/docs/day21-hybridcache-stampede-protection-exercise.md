# Day 21 — Exercise answer

> Paste the cache wiring + the load-test before/after (DB queries/sec, p99).
> Show stampede protection working under concurrency.

**What this builds:** HybridCache (.NET 9+)

Three parts, in that order, then what the numbers do not say.

---

## 1. The cache wiring

`QuotesApi/Extensions/CachingExtensions.cs` — comment blocks elided for length,
otherwise verbatim:

```csharp
    public static IServiceCollection AddCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<CacheMetrics>();
        services.AddSingleton<DbCommandCounterInterceptor>();

        var options = configuration
            .GetSection(CacheOptions.SectionName)
            .Get<CacheOptions>() ?? new CacheOptions();

        if (!options.Enabled)
        {
            services.AddSingleton<ICacheGeneration, InMemoryCacheGeneration>();
            services.AddSingleton<IQuoteListCache, PassThroughQuoteListCache>();
            return services;
        }

        var redisConnectionString = options.Redis.ConnectionString;
        var useRedis = !string.IsNullOrWhiteSpace(redisConnectionString);

        if (useRedis)
        {
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

                configurationOptions.AbortOnConnectFail = false;

                redis.ConfigurationOptions = configurationOptions;
                redis.InstanceName = options.Redis.InstanceName;
            });

            services.AddSingleton<ICacheGeneration, DistributedCacheGeneration>();
        }
        else
        {
            services.AddSingleton<ICacheGeneration, InMemoryCacheGeneration>();
        }

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
```

Four decisions in that method are worth pointing at.

**The instruments register unconditionally, the cache does not.** `CacheMetrics`
and `DbCommandCounterInterceptor` are added before the `Enabled` check, because
the whole exercise is a before/after comparison and the "before" is the run with
`Cache:Enabled=false`. If the counters only existed when caching was on, the
baseline could not be measured with the same instrument as the result — and
comparing two different instruments is not a comparison.

**`Enabled=false` resolves a pass-through, not a branch in the endpoint.**
`PassThroughQuoteListCache` reads straight to the repository. So the cached and
uncached paths run the same projection and cannot drift, which is what makes
"the cached response is byte-identical" a property of one code path rather than
two.

**An empty Redis connection string means L1 only — not localhost.** A string
that is present but unparseable fails fast, on the line where the decision is
taken, with a message naming the key and its double-underscore spelling. Day 20
taught this: `ServiceBus:Enabled=true` with an empty namespace threw from forty
frames inside the Azure SDK, naming the SDK's own parameter, because the startup
validator could not run before the client was constructed.

**`AddHybridCache` picks up whatever `IDistributedCache` is registered.** So the
Redis branch above is the *only* place that decides one level or two — there is
no second switch to keep in step with it.

### The read path

`HybridQuoteListCache.GetPageAsync` — this is the whole of the stampede
protection:

```csharp
metrics.RecordRequest(CacheKeys.QuoteListFamily);

// Deep pages are not cached: `page` is unbounded by the endpoint's validation,
// so caching every page a caller cares to name would let them mint entries
// without limit.
if (page > _options.MaxCachedPage)
{
    metrics.RecordBypass(CacheKeys.QuoteListFamily);
    return await LoadDirectAsync(page, size, cancellationToken);
}

var token = await generation.GetAsync(cancellationToken);
var key = CacheKeys.QuoteList(token, page, size);
metrics.RecordKey(key);

return await cache.GetOrCreateAsync(
    key,
    (page, size),
    (state, ct) => LoadAsync(state, ct),
    _entryOptions,
    tags: null,
    cancellationToken);
```

No `SemaphoreSlim` per key, no double-checked locking, no lock dictionary to
leak. `GetOrCreateAsync` deduplicates concurrent callers for the same key: one
runs the factory, the rest await its result.

`IQuoteListCache` exposes **one read-through method and no Get/Set pair**,
deliberately — an interface with `TryGet` and `Set` invites the shape that
stampedes, and no care at the call site fixes it, because the race is between
the check and the set:

```csharp
// The bug HybridCache removes. Under 100 concurrent requests on a cold key,
// all 100 miss, all 100 run the query.
if (!cache.TryGetValue(key, out var page))
{
    page = await repository.GetPagedAsync(...);   // <- 100 of these
    cache.Set(key, page, ttl);
}
```

**The factory opens its own scope.** `HybridQuoteListCache` is a singleton
taking `IServiceScopeFactory`, not a scoped service holding a repository. The
factory is shared by every concurrent caller, so it must not capture the scoped
`DbContext` of whichever request arrived first: if that request is cancelled or
its scope disposed while others await, a captured context fails for all of them
— an `ObjectDisposedException` under load that never reproduces in a
single-request test.

### The endpoint, and the key

```csharp
// GET /api/quotes — no `if (cacheEnabled)` here, on purpose.
var result = await cache.GetPageAsync(page, size, cancellationToken);
return Results.Ok(result);
```

Key shape, as it appears in Redis:

```
quotes:list:v1:g{generation}:p{page}:s{size}
```

The leading `quotes:` comes from `InstanceName`; `v1` is the DTO contract
version; `g{generation}` is the invalidation mechanism — a token in the key, so
a bump makes every previously written key unaddressable **including L1**, which
`RemoveByTagAsync` cannot reach.

Cardinality is bounded on both axes: `size` by the endpoint's existing
validation (which runs *before* the key is built), and `page` by
`Cache:MaxCachedPage`. The plan claimed `MaxPageSize` covered both; it does not
— `?page=999999` is a valid request that would mint a fresh entry, and a caller
walking the page number would mint them without limit.

---

## 2. Load test, before and after

`Day21/scripts/measure-cache.ps1` — the same load twice, changing exactly one
thing. 5,000 requests, 100 concurrent connections, 5 pages of size 20, against
**2,020 rows in both runs**.

Evidence: `../verification/raw-load-test.txt` is the unedited console output —
every bombardier block, the row counts, and the RESULT table.
`../verification/day21-measurement-run.txt` reads it into the tables below.

### DB queries/sec

One page read costs two queries — a `COUNT(*)` and a paged `SELECT` — so
queries/sec is the achieved request rate × 2 while the cache is off.

| page | req/sec off | **DB queries/sec off** | req/sec on | DB queries on |
|---|---|---|---|---|
| 1 | 453 | **906** | 4,973 | 2, once |
| 2 | 416 | **832** | 4,979 | 2, once |
| 3 | 453 | **906** | 4,955 | 2, once |
| 4 | 453 | **906** | 4,990 | 2, once |
| 5 | 498 | **996** | 4,956 | 2, once |

Sustained: **~830–1,000 queries/sec** off, **0** on after the five cold misses.
Totals: **10,000 queries** off, **10** on.

The right way to read the right-hand column is that it is not a rate at all.
With the cache on the database is touched **five times in the entire run** — once
per key — and then never again however many requests arrive. Queries/sec does
not fall from ~900 to a smaller number; it **decouples from the request rate
entirely**. Averaging 10 queries into a "per second" figure would produce a
number that means nothing.

### p99 latency

| page | p50 off | **p99 off** | p50 on | **p99 on** |
|---|---|---|---|---|
| 1 | 191.38ms | **366.20ms** | 6.67ms | **117.01ms** |
| 2 | 222.83ms | **404.80ms** | 4.77ms | **77.89ms** |
| 3 | 206.18ms | **332.58ms** | 7.09ms | **46.21ms** |
| 4 | 199.92ms | **353.60ms** | 7.07ms | **52.21ms** |
| 5 | 190.26ms | **317.25ms** | 4.26ms | **38.43ms** |

Worst page: p99 **404.80ms → 117.01ms**. All 10,000 responses 2xx, both runs.

**The cached p99 is the interesting column, and it is not noise.** At 38–117ms
against a cached p50 of 4–7ms, the gap *is* the cold miss: whichever callers
arrive first wait on the single factory invocation while the other ~995 are
served from memory. A cache does not remove the first read. It stops the first
read happening a thousand times — and the p99 is where that first read shows up.

Page 1 has the worst cached p99 and the slowest ramp in the uncached progress
output (209/s climbing to 453/s): process warm-up, not anything about page 1.
Its *uncached* p99 is not the worst, though — page 2's is. An earlier run of
this script had page 1 worst on both columns, and stating that as a rule would
have been reading a pattern into one sample.

### One measurement bug found, and its direction

The first version of the script did not delete `quotes.db` between runs, and
`/api/diagnostics/seed` **appends**. So the baseline read 2,020 rows and the
cached run read 4,020 — the exact mistake the script's own comments say it
avoids.

The direction is what mattered: a bigger table slows the **uncached** path and
barely touches the cached one, so the bug **flattered the cache**. The script now
deletes the database before each run, prints `rows in Quotes` for both, and
refuses to report at all if the two counts differ. A measurement whose bug
points at its own conclusion is worse than no measurement.

---

## 3. Stampede protection under concurrency

### The number

**5 misses over 5 keys, at 100 concurrent requests each.** One database load per
key, not one per request. 1,000 concurrent callers for page 1 produced **one**
factory invocation; without protection the same load produces 1,000.

| | Cache OFF | Cache ON |
|---|---|---|
| Requests | 5,000 | 5,000 |
| Misses | 5,000 | **5** |
| Hit rate | 0% | **99.90%** over 5 distinct keys |
| DB queries | 10,000 | **10** |

The 1000:1 reduction is exactly the concurrency per key. Not a coincidence — it
*is* the concurrency, which is what makes this a measurement of stampede
protection rather than of caching in general.

### Asserted in CI, with a control

| Test | What it pins |
|---|---|
| `Cold_cache_under_100_concurrent_requests_hits_the_database_once` | 100 concurrent, all 200 OK, identical bodies, **2** DB queries, 1 miss, 99 hits, 1 key |
| `The_same_load_without_the_cache_fans_out_to_the_database` | The same load, cache off: **200** DB queries |
| `Concurrent_requests_for_different_pages_each_load_once` | Dedup is per key: 10 pages × 10 requests → 10 misses, not 1 and not 100 |

The control is the half that makes the first a measurement. On its own, "100
concurrent requests caused 2 queries" proves the number is 2 — not that it used
to be 200.

The expected count is **2, not 1**, because one page read is two queries.
Asserting 1 would have been asserting a misunderstanding of the code under test.

Both run without Docker, so the headline evidence is in CI rather than gated
behind infrastructure.

### Its honest boundary

Deduplication is **per process**. With N instances and a cold key you get up to
N factory invocations, not one. Still an N-fold reduction from N × concurrency,
and it is the number to state rather than round down to "one".

### The distributed half

The load run above is L1 only. L2 is proved separately by
`Quotes.Tests.Integration.Redis` (Testcontainers, 3/3): **two hosts, one Redis,
deliberately different databases**. Host B answers `total: 20` — a payload its
own 21-row database could not have produced — with `Misses == 0`. Two hosts
sharing a database would prove nothing: a "hit" would be indistinguishable from
reading the same rows.

| Test | What it establishes |
|---|---|
| `An_entry_written_by_one_host_is_served_to_another` | L2 is genuinely shared |
| `A_write_on_one_host_invalidates_the_entry_for_the_other` | The generation bump crosses instances — which tag removal could not do, since it cannot reach host B's in-process memory |
| `The_entry_lands_in_redis_under_the_designed_key` | The key in Redis is exactly `quotes:list:v1:g0:p1:s20`, a write persists the generation token, and every key carries the instance prefix once |

`LocalCacheExpiration` is **500ms** in that suite against ten minutes elsewhere:
with a long L1 a second read on the same host would come from memory and the
suite could never observe L2 at all — every assertion about Redis would pass for
the wrong reason.

Degradation is covered without Docker: `RedisUnavailableTests` points the cache
at a closed port and asserts reads still succeed, L1 still serves hits, and a
write still commits when the generation cannot be persisted.

---

## What these numbers do not show

- **The latency drop is flattered by a local SQLite file.** A local read is
  already fast, so the ~30–45x p50 improvement is mostly removed EF materialisation
  and JSON, not a removed network hop. Against a database across a network the
  queries/sec figure would be identical and the latency figure much better. The
  honest headline here is the query count, not the milliseconds.
- **Five keys is a friendly workload.** Real traffic spreads over more keys and
  includes writes that invalidate. 99.9% over 5 keys is a statement about
  deduplication, not a production forecast — which is why the script reports the
  key count beside the ratio and defaults to 5 pages rather than 1.
- **One process.** Per-instance dedup, so N instances mean up to N loads.
- **Staleness is accepted, not eliminated.** `LocalCacheExpiration` (30s
  default) is the window in which an instance may serve a page written before an
  invalidation it has not yet observed; the generation token itself propagates
  within `GenerationCacheDuration`, one second.
- **Nothing was tuned.** Expirations and `MaxCachedPage` are reasoned defaults,
  not measured ones. `MaximumPayloadBytes` bounds one entry, not total L1 size.
- **A new failure mode was introduced.** "Every read hits the database" is gone;
  "the cache is serving stale data and nothing is erroring" is now possible.
  `/api/cache/stats` and the `cache.hit_ratio` gauge exist for that, and no
  alert is wired.

## What this exercise turned up elsewhere

**`ConfigureAppConfiguration` in a `WebApplicationFactory` runs too late for
registration-time reads.** `AddCaching` decides which `IQuoteListCache` to
register from configuration, and `Program.cs` reads that while composing the
builder — before the factory's callbacks are applied at `builder.Build()`. So
the first `CachedQuotesApiFactory` set `Cache:Enabled=true` and the app
registered the pass-through anyway. The cache is now wired through
`ConfigureServices`, which demonstrably runs after the app's own registrations.

**That also means Day 20's claim was wrong.** `QuotesApiFactory` pins
`Outbox:RelayEnabled=false` the same way, with a comment saying it "sits above
environment variables in the precedence chain and cannot be overridden by
them". It does not, for anything read at registration time — the
`[ModuleInitializer]` environment variable was doing the work all along. The
comment is corrected rather than left to mislead the next person.

**Both scripts leaked the process they were meant to kill.** `dotnet run` spawns
the app as a child, so the handle `Start-Process` returns is the launcher. A
`QuotesApi.exe` survived a run holding a lock on the build output, and the next
build failed with MSB3027. For Day 21 that was an annoyance; for **Day 20's
crash proof it was a hole in the evidence** — if a force kill leaves the app
alive, the restart cannot bind the port, or the old relay keeps running and
publishes, and the proof measures the wrong thing. That run passed, so the child
did die — but by accident: killing the parent breaks the redirected stdout pipe
and the app crashes writing to it. Both scripts now launch the DLL directly.

**The cache key namespaced itself twice.** `InstanceName` prefixes everything the
distributed cache writes, and `CacheKeys` was adding `quotes:` as well — so the
real key was `quotes:quotes:list:v1:g0:p1:s20`, one segment longer than every
document described. Found by the Redis suite reading an actual key rather than
trusting the design.

**The shared in-memory SQLite connection cannot serve concurrent requests.**
`QuotesApiFactory` keeps one connection open because a `:memory:` database
exists only while a connection to it does, and every test until now was
sequential. The 100-concurrent test was the first to hammer it and produced a
wall of HTTP 500s in **both** the cached and uncached runs — which briefly looked
like a cache bug. The cache tests use a per-factory file-backed database.
