# Day 21 — HybridCache + stampede protection

## Task prompt

> Add `HybridCache` (in-memory + Redis) to a hot read, with stampede protection
> so a cache miss doesn't fan out N identical DB hits. Measure the hit rate and
> the DB load drop under concurrent load.

**What this builds:** HybridCache (.NET 9+)

## Exercise

> Paste the cache wiring + the load-test before/after (DB queries/sec, p99).
> Show stampede protection working under concurrency.

## Where the answer is

| Document | What it is |
|---|---|
| `day21-hybridcache-stampede-protection-exercise.md` | The answer: the cache, the stampede protection, and the measurement |
| `day21-hybridcache-stampede-protection-implementation-plan.md` | The plan, written before any of it was built, with a banner listing the five things that came out differently |
| `../scripts/measure-cache.ps1` | The measurement, runnable: same load twice, only `Cache:Enabled` changes |
| `../verification/raw-load-test.txt` | Unedited console output of the run the answer is written against |
| `../verification/day21-measurement-run.txt` | That run read into tables, with the commentary |

## Headline

| | Cache OFF | Cache ON |
|---|---|---|
| Requests | 5,000 | 5,000 |
| DB queries/sec (sustained) | ~830–1,000 | **0** after 5 cold misses |
| DB queries, total | 10,000 | **10** |
| p99, worst page | 404.80ms | **117.01ms** |
| Misses | 5,000 | **5** |
| Hit rate | 0% | 99.90% over 5 keys |

Five misses over five keys, at 100 concurrent requests each. One database load
per key rather than one per request — which is what stampede protection means,
stated as a number rather than argued for.

## Starting point

There was no caching anywhere in this application before Day 21 — verified
rather than assumed: `IMemoryCache`, `IDistributedCache`, `HybridCache`,
`OutputCache`, `ResponseCaching` and `AddStackExchangeRedis` appeared in **zero**
files across `Day7/piece2`. Every read went to the database, every time.
