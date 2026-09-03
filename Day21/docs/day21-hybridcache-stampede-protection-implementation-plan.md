# Day 21 — HybridCache + stampede protection

## Detailed task prompt

> Add `HybridCache` (in-memory + Redis) to a hot read, with stampede protection
> so a cache miss doesn't fan out N identical DB hits. Measure the hit rate and
> the DB load drop under concurrent load.

## What changed once it was built

The plan below is kept as written. Five things came out differently, and one of
them was a plain error in the plan:

**1. The plan's cardinality claim was wrong.** It said
`PaginationOptions.MaxPageSize` bounds key cardinality. It bounds `size` and
says nothing about `page` — the endpoint only rejects `page < 1`, so
`?page=999999` is a valid request that mints a fresh cache key, and a caller
walking the page number mints them without limit. Every such entry is empty,
none is ever read again, and the memory is not reclaimed until expiry: a
denial-of-service vector wearing a cache's clothes. Closed with
`Cache:MaxCachedPage` (default 20) — pages past it are served straight from the
database, which is the right place for a read that is by definition not hot.
Cardinality is now at most `MaxCachedPage × MaxPageSize`, a number that can be
stated.

**2. Generation-stamped keys are the design, not the fallback.** The plan had
tags preferred with a generation counter as a contingency if
`RemoveByTagAsync` turned out to be inert. That framing undersold it: tag
removal can clear the distributed layer but **cannot reach into another
instance's L1 memory**, so every replica would keep serving its own copy until
`LocalCacheExpiration` — thirty seconds of stale reads after a write, silently.
Because the generation is part of the *key*, a bump invalidates L1 too: the
entry is still in memory, nothing asks for it. Other instances notice within
`GenerationCacheDuration` (one second). Strictly better, and independent of what
the cache implementation supports.

**3. The generation is a token, not a counter.** Invalidation only requires the
value to *change*, never to increase — two concurrent writers both write a new
token, and whichever lands, readers' old token no longer matches. So it needs no
atomic `INCR`, which means no direct `StackExchange.Redis` dependency:
`IDistributedCache.GetString`/`SetString` is enough.

**4. One page read is two database commands, not one.** `GetPagedAsync` issues a
`COUNT(*)` and a paged `SELECT`. The stampede assertion is therefore
`Be(2)`, not `Be(1)`; asserting 1 would have been asserting a misunderstanding
of the code under test. Both statements carry the `TagWith` tag, because the
measurement is of database load rather than of query count.

**5. The cache service is a singleton that opens its own scope.** The factory
can be shared by every concurrent caller — that is the whole point — so it must
not capture the scoped `DbContext` of whichever request arrived first. If that
request is cancelled or its scope disposed while others await, a captured
context fails for all of them: an `ObjectDisposedException` under load that
never reproduces in a single-request test.

## Branch base

Branched off `main` at `54c3b1a`, which already contains Day 20 (PR #48 merged),
so `Day7/piece2` has the outbox and this branch starts from the current code.

## Goal

There is no caching in this application at all — verified rather than assumed:
`IMemoryCache`, `IDistributedCache`, `HybridCache`, `OutputCache`,
`ResponseCaching` and `AddStackExchangeRedis` appear in **zero** files across
`Day7/piece2`. Every read goes to SQLite or SQL Server, every time.

Day 21 adds one cache, to one endpoint, and — the part that actually carries the
day — measures what it did.

## What the exercise is really about

`HybridCache` is not the interesting half. Registering it is three lines, and it
does stampede protection natively: concurrent callers for the same missing key
share **one** factory invocation while the rest await the result. Nothing here
needs to be hand-built.

The interesting half is that "the cache works" is a claim, and the task asks for
a measurement. That means the deliverable is really three things:

1. A cache on a read that is genuinely hot, keyed so that it can actually hit.
2. An instrument that counts **database commands**, not cache calls — because
   "DB load dropped" is a statement about the database, and counting our own
   cache hits proves nothing about it.
3. A concurrent load that would fan out without protection, run against a
   **cold** key, so the stampede is real rather than theoretical.

The naive comparison worth spelling out, because it is what stampede protection
replaces:

```csharp
// The bug HybridCache removes. Under 100 concurrent requests on a cold key,
// all 100 miss, all 100 run the query, and the database takes 100 hits for
// one piece of information.
if (!cache.TryGetValue(key, out var page))
{
    page = await repository.GetPagedAsync(...);   // <- 100 of these
    cache.Set(key, page, ttl);
}
```

## Which read, and why that one

| Candidate | Verdict |
|---|---|
| `GET /api/quotes?page&size` | **Chosen.** Shared across all callers, so one cached entry serves everyone. Key inputs are two bounded integers, so key cardinality is small and the hit rate can actually be high. It is the app's only truly public list read. |
| `GET /api/collections` | Rejected. Per-owner data, so the key must include the caller id — cardinality scales with users and the hit rate collapses. It is also the endpoint whose N+1 Day 5 already fixed; caching it would hide query work rather than avoid it. |
| `GET /api/quotes/{id}` | Rejected as the primary. High cardinality, already a single indexed lookup. Worth adding later as a second key family, not as the thing being measured. |
| `/api/diagnostics/authors-quotes-*` | Rejected. Expensive enough to make a dramatic graph, but Development-only routes that no real client calls — a demo, not a hot read. |

`GET /api/quotes` also has the property that makes a cache measurable: its cost
is two round trips (a `COUNT(*)` and a paged `SELECT`, see
`QuoteRepository.GetPagedAsync`), so a hit saves a countable, non-trivial amount
of database work rather than a single primary-key read.

## Architecture

### 1. What gets cached is a DTO, not the entity

`GetPagedAsync` returns `(IReadOnlyList<Quote>, int Total)`. Caching `Quote`
directly is the mistake that looks like a shortcut:

- The cache format becomes the EF model. A property added for persistence
  reasons silently changes what is serialised, and old entries deserialise into
  a shape the new code did not expect.
- Entities come out of the cache detached but indistinguishable from tracked
  ones, which invites someone downstream to mutate one and call `SaveChanges`.

So a `QuoteListPage` record (`Page`, `Size`, `Total`, `Items` of
`QuoteListItem`) is defined as the cache contract, and the endpoint's response
shape is projected from it. It is versioned in the key (below) precisely so a
change to it cannot be read by old code.

### 2. Key design

```
quotes:list:v1:g{generation}:p{page}:s{size}
```

- `v1` — the DTO contract version. Bumped when `QuoteListPage` changes shape, so
  a deploy cannot read entries written by the previous build. Cheaper and safer
  than a migration for cache data.
- `g{generation}` — the invalidation mechanism (see below).
- `p`/`s` — the only two request inputs. Bounded by
  `PaginationOptions.MaxPageSize`, which the endpoint already validates *before*
  the cache is consulted. That ordering matters: validating after would let an
  unbounded `size` become an unbounded number of cache keys, which is a
  memory-exhaustion vector dressed as a cache.

### 3. Two levels, and why the local TTL is the shorter one

`HybridCache` is L1 in-process plus L2 distributed:

| Level | Where | Expiration | Why |
|---|---|---|---|
| L1 | Per-instance memory | `LocalCacheExpiration` ≈ 30s | Cannot be invalidated from another instance. This number **is** the stale window you are choosing to accept. |
| L2 | Redis, shared | `Expiration` ≈ 5m | Invalidatable, shared across instances, survives a restart. |

L1 shorter than L2 is not a detail. An invalidation can clear Redis but cannot
reach into another instance's memory, so every instance may serve a stale page
for up to `LocalCacheExpiration` after a write. Setting L1 to five minutes
because "it's faster" buys latency and pays for it with five minutes of
staleness on a list that changes.

### 4. Invalidation — the part with a real decision in it

A write to any quote invalidates every cached page, because a create can shift
every subsequent page and a delete can change `Total`. Two mechanisms:

**Preferred: tags.** `HybridCache` exposes `RemoveByTagAsync`, and entries would
be written with a `"quotes"` tag. **This must be verified against the pinned
package version before relying on it** — the API surface has existed longer than
a working default implementation of it, and a `RemoveByTagAsync` that silently
does nothing is the worst possible outcome here: writes would appear to
invalidate and the endpoint would serve stale data until natural expiry, with
nothing logged. The verification is a test, not a reading of the release notes:
write an entry, tag it, remove by tag, assert the next read misses.

**Fallback if tags are inert: a generation counter.** A single integer in Redis,
`INCR`-ed on every quote write, included in the key. Old keys become unreachable
and expire on their own. Provider-agnostic, needs no tag support, and one round
trip per write — which the write path can afford, since Day 20 already put an
outbox row in that transaction.

Where the invalidation is triggered matters and follows Day 20's shape:
`QuoteWriteService` already owns the write transaction, so the cache bump goes
**after** a successful commit, next to `signal.Notify()`. Bumping inside the
transaction would invalidate for a write that then rolled back.

### 5. Stampede protection, and its honest boundary

`HybridCache` deduplicates concurrent factory invocations **per instance**. With
N app instances and a cold key, you get up to N database hits, not one. That is
still an N-fold reduction from N×concurrency, and it is the number to state
rather than round down to "one".

Nothing is hand-rolled: no `SemaphoreSlim` per key, no double-checked locking.
If the measurement shows fan-out, the cause will be a key that varies per
request (a common mistake: including a timestamp, a correlation id, or an
`IOptionsSnapshot` value that differs per call), not missing locking.

### 6. Measurement — the deliverable

Three instruments, because each answers a different question.

**Hit rate.** `HybridCache` does not tell the caller whether a hit occurred, so
it is inferred where the truth is: inside the factory. The factory ran ⇒ it was
a miss. A `CacheMetrics` class (same shape as Day 20's `OutboxMetrics`, meter
registered in `ObservabilityExtensions`) carries:

- `cache.requests` — counter, tagged `key_family="quotes.list"`
- `cache.misses` — counter, incremented inside the factory
- `cache.hit_ratio` — observable gauge, computed from the two
- `cache.factory.duration` — histogram, the cost a hit avoids

If the pinned `Microsoft.Extensions.Caching.Hybrid` package emits its own meter
with hit/miss counters, that becomes the source of truth and these become the
cross-check. Verify at implementation time; do not assume either way.

**DB load.** The number the task actually asks for, and the one our own counters
cannot establish. An EF `DbCommandInterceptor` counts executed commands,
distinguishing the quotes-list queries (matched by EF's command source /
`CommandSource.LinqQuery` plus a tag applied via `TagWith`) from everything
else. Exposed as `db.commands` (counter, tagged by query family) and read back
through a diagnostics endpoint.

`TagWith("quotes-list")` on the query in `GetPagedAsync` is what makes the
interceptor's classification robust — matching on SQL text is a string-matching
trick that breaks the first time the query changes.

**The load itself.** Two forms, deliberately:

- A deterministic integration test — the primary evidence, because it runs in
  CI with no Docker and cannot be fudged. Cold cache, 100 concurrent
  `GET /api/quotes?page=1&size=20`, assert: 100 responses, **1** quotes-list DB
  command pair, 99 hits.
- A real load run for the write-up — `bombardier` or `k6` against a running
  instance with Redis, before and after, reporting p50/p95/p99 and the DB
  command count from the diagnostics endpoint. A test proves the mechanism; a
  load run shows the shape of the win.

### 7. Configuration

New `Cache` section (`CacheOptions`, `ValidateDataAnnotations` +
`ValidateOnStart`, mirroring `OutboxOptions`):

| Key | Default | Meaning |
|---|---|---|
| `Cache:Enabled` | `false` | Whether the read path consults the cache at all |
| `Cache:Expiration` | `00:05:00` | L2 (Redis) entry lifetime |
| `Cache:LocalCacheExpiration` | `00:00:30` | L1 lifetime — the accepted stale window |
| `Cache:MaximumPayloadBytes` | `1048576` | Entries larger than this are not cached |
| `Cache:Redis:ConnectionString` | `""` | Empty ⇒ L1 only, no Redis, no connection attempted |

`Enabled=false` by default is the Day 20 lesson applied without having to relearn
it: every existing test asserts uncached behaviour, and a cache switched on
underneath them would make `QuoteEndpointTests` pass or fail depending on
execution order. The cache tests turn it on explicitly. `[ModuleInitializer]`
pins in each test project's `TestEnvironment` will clear `Cache__Enabled` from
the ambient environment for the same reason `Outbox__RelayEnabled` is cleared —
that leak cost seven failing tests on Day 20 and should not be rediscovered.

An empty Redis connection string must mean "L1 only", not "try localhost". Day
20's crash-proof run died on exactly this shape of bug: `ServiceBus:Enabled=true`
with an empty namespace threw from inside the Azure SDK, forty frames deep,
because the guard was unreachable. So `AddCache` checks the string eagerly, on
the line where the decision is made, and says which key to set.

### 8. When Redis is down

A distributed cache that can fail the request is worse than no cache. Expected
behaviour, to be tested rather than hoped for: L2 unavailable ⇒ `HybridCache`
falls back to L1 and the factory, the request succeeds, and the failure is
logged once per interval rather than per request. `StackExchange.Redis` will
also be configured with `AbortOnConnectFail = false` so a Redis that is down at
startup does not prevent the app from starting.

## Tools required

### NuGet packages

| Package | Project | Purpose |
|---|---|---|
| `Microsoft.Extensions.Caching.Hybrid` | `QuotesApi` | `HybridCache` itself, and the stampede protection |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | `QuotesApi` | The L2 `IDistributedCache` implementation |
| `Testcontainers.Redis` | `Quotes.Tests.Integration.Redis` (new) | A real Redis for the L2 tests |

Pin each to whatever `dotnet add package` resolves for `net10.0` — nuget.org is
not reachable from where this plan was written, so no version is asserted here.
`StackExchange.Redis` arrives transitively; `Testcontainers.Redis` should match
the `4.1.0` already used by the MsSql and Service Bus suites, or all three move
together.

### Docker images

| Image | Used by |
|---|---|
| `redis:7-alpine` | Local `docker run` for a real two-level run, and `Testcontainers.Redis` for the L2 suite |

### Load generation — pick one

| Tool | Install | Note |
|---|---|---|
| `bombardier` | `winget install bombardier` / GitHub release | Single binary, `-c` concurrency, `-n` requests, prints latency percentiles. Simplest thing that produces a defensible number. |
| `k6` | `winget install k6` | Scriptable stages, useful if the run should ramp rather than slam |
| `hey` | Go install | Equivalent to bombardier; use whichever is already present |

**Not PowerShell.** Windows PowerShell 5.1 has no `ForEach-Object -Parallel`,
and `Start-Job` spawns a process per request — the harness becomes the
bottleneck and the "concurrent" load is not concurrent. The deterministic
concurrency lives in the integration test, where `Task.WhenAll` over one
`HttpClient` is honest.

### Observability and inspection

| Tool | Purpose |
|---|---|
| OpenTelemetry metrics | Already wired (`ObservabilityExtensions.WithMetrics`); the new meter registers alongside `OutboxMetrics` |
| Jaeger (`jaegertracing/all-in-one`) | Already used since Day 4 — a cache hit should show as a request span with **no** EF child spans, which is the visual proof |
| `redis-cli` (in the container) or RedisInsight | Confirm the keys are what the design says: `redis-cli --scan --pattern 'quotes:list:*'`, and `TTL` on one |
| App Insights | Optional; the meter flows there when configured |

### Already present, reused

.NET 10 SDK · Docker Desktop · `dotnet-ef` · Day 11's `/api/diagnostics/seed`
and `/stats` for a data volume worth reading · `QuotesApiFactory`'s
DI-swap pattern · xUnit + FluentAssertions · `IClock`

## Planned file changes

**New — `Day7/piece2/QuotesApi`**

```
Caching/CacheOptions.cs
Caching/CacheKeys.cs                  key construction + the version constant
Caching/CacheMetrics.cs               requests / misses / hit ratio / factory duration
Caching/IQuoteListCache.cs
Caching/HybridQuoteListCache.cs       the factory, and where a miss is counted
Caching/ICacheGeneration.cs
Caching/RedisCacheGeneration.cs       INCR-based generation (invalidation fallback)
Models/QuoteListPage.cs               the cache contract DTO
Observability/DbCommandCounterInterceptor.cs
Extensions/CachingExtensions.cs       AddCaching + GET /api/cache/stats
```

**Modified**

```
Extensions/QuoteEndpointExtensions.cs      GET /api/quotes reads through the cache
Extensions/InfrastructureExtensions.cs     AddCaching, interceptor registration
Extensions/ObservabilityExtensions.cs      register the cache meter
Repositories/QuoteRepository.cs            TagWith on the list query
Services/QuoteWriteService.cs              invalidate after commit
Program.cs                                 MapCacheEndpoints
appsettings.json                           the Cache section
```

**Tests**

```
Quotes.Tests.Unit/Caching/CacheKeysTests.cs
Quotes.Tests.Unit/Caching/HybridQuoteListCacheTests.cs
Quotes.Tests.Integration/QuoteListCacheTests.cs          hit/miss, invalidation
Quotes.Tests.Integration/CacheStampedeTests.cs           the headline test
Quotes.Tests.Integration.Redis/                          new project, L2 behaviour
```

## Implementation sequence

1. `QuoteListPage` DTO + `CacheKeys` + unit tests. No cache yet — get the
   contract and the key shape right first, because both are hard to change once
   entries exist.
2. `DbCommandCounterInterceptor` + `TagWith` + `/api/cache/stats`. **The
   instrument before the thing it measures**, so the baseline is recorded
   against the uncached endpoint rather than reconstructed afterwards.
3. Baseline measurement: seed, load, record DB commands and percentiles with
   `Cache:Enabled=false`. Written down before any cache exists.
4. `AddCaching` with L1 only (no Redis) + `HybridQuoteListCache` + the endpoint
   change. Prove hits and the stampede test at this point — stampede protection
   is an L1 property and does not need Redis to be demonstrated.
5. Redis as L2, with the eager connection-string guard.
6. Invalidation: verify `RemoveByTagAsync` actually works; implement the
   generation fallback if it does not. Test that a write makes the next read
   miss.
7. Redis-down behaviour.
8. Load run with cache on; compare to step 3's baseline.
9. Write-up against the numbers, not against this plan.

## Test strategy

### Unit

- Key construction is stable, includes the version and generation, and differs
  by page and size.
- The factory is invoked exactly once for one logical read; a second read with
  the same key does not invoke it.
- A miss increments `cache.misses`; a hit does not.

### Integration, in-process (no Docker)

- **`Cold_cache_under_100_concurrent_requests_hits_the_database_once`** — the
  headline. `Task.WhenAll` of 100 `GET /api/quotes?page=1&size=20`, all 200 OK
  and identical bodies, quotes-list DB command count **1**, misses 1, hits 99.
- The same 100 requests with the cache disabled hit the database 100 times —
  the control, without which the first test proves only that the number is 1
  and not that it used to be 100.
- A hit returns a body byte-identical to the miss.
- Creating a quote makes the next read miss, and the new quote is in it.
- Different `page`/`size` are different keys and each miss once.
- `size` beyond `MaxPageSize` is rejected **before** any cache key is built —
  guarding the cardinality vector.

### Integration, Redis (Docker)

- An entry written by one host is readable by a second host with its own L1 —
  which is the only thing that proves L2 is actually shared.
- `RemoveByTagAsync` (or the generation bump) is visible to both hosts.
- Redis stopped mid-run: requests still succeed, served by L1 and the factory.

## Measurement protocol

The write-up reports these, all from the same run, with the raw output attached:

| Metric | Where from |
|---|---|
| Cache hit rate | `cache.requests` and `cache.misses` over the run |
| DB commands, cache off | `db.commands{family=quotes-list}` — the baseline from step 3 |
| DB commands, cache on | Same counter, same load |
| Reduction | Stated as a ratio with both absolute numbers, never as a bare percentage |
| p50 / p95 / p99 | The load tool's own output |
| Stampede factor | Concurrency ÷ factory invocations on a cold key |

Two rules for reporting, both of which exist because it is easy to produce a
flattering number by accident:

1. **A hit rate without the key count is meaningless.** 99% on one key is a
   warm loop, not a result. The number of distinct keys touched is reported
   next to it.
2. **The baseline must come from the same seeded dataset.** Measuring the
   baseline on 20 rows and the cached run on 20,000 measures the seed, not the
   cache.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| `RemoveByTagAsync` is inert on the pinned version — writes appear to invalidate and do not | Tested explicitly before being relied on; generation-counter fallback ready |
| Stale reads after a write, for up to `LocalCacheExpiration`, on other instances | Stated as the accepted trade with the number attached; L1 kept short |
| Key cardinality explosion via `size` | Validation runs before key construction; `MaxPageSize` bounds it; asserted by test |
| Cached entity shape drifting from the EF model | A dedicated DTO plus a version prefix in the key |
| Redis down taking the endpoint with it | `AbortOnConnectFail=false`, L1 fallback, tested with Redis stopped |
| Measuring a warm cache and calling it a hit rate | Cold-start asserted by the test; distinct key count reported alongside |
| A cache switched on under existing tests | `Enabled=false` default plus `[ModuleInitializer]` pins in every test project that boots the app |
| An empty Redis connection string producing a 40-frame SDK exception at startup | Eager guard naming the config key, exactly as Day 20 added to `AddMessaging` |

## Acceptance criteria

1. `GET /api/quotes` serves from cache when `Cache:Enabled=true`, and its
   response is byte-identical to the uncached response.
2. 100 concurrent requests on a cold key produce **one** quotes-list database
   round trip; the same load with the cache off produces 100.
3. A quote write makes the next read miss and return the new data.
4. Redis unavailable degrades to L1 without failing a request.
5. The whole suite is green with the cache off, and nothing new requires Docker
   to pass CI.
6. Hit rate, DB command counts before and after, and latency percentiles are all
   reported from one run, with the distinct-key count beside the hit rate.
7. A cache hit shows in Jaeger as a request span with no EF child spans.

## What this will not prove

- **Not multi-instance stampede protection.** Deduplication is per instance; N
  instances mean up to N factory invocations on a cold key. The tests run one
  host, so the measured factor is per-instance.
- **Not production hit rates.** A synthetic load over a handful of pages is a
  best case. Real traffic spreads over more keys and includes writes that
  invalidate.
- **Not a latency claim for Redis.** L2 will be a container on the same machine;
  a real network hop to Azure Cache for Redis is slower than that, and can make
  L2 slower than the database for a cheap query.
- **Nothing about memory ceilings.** `MaximumPayloadBytes` bounds one entry, not
  total L1 size. Sizing that needs a measurement this exercise does not include.
