# Diagnosing a slow endpoint from its trace

`GET /api/collections`, diagnosed from the Jaeger trace, fixed, and confirmed
fixed by a second trace.

## Reproducing

With Jaeger running and the API started:

```powershell
cd scripts
.\seed-and-hit.ps1                       # 15 collections, 5 requests
.\seed-and-hit.ps1 -Collections 30       # 30 -- watch the span count follow
```

Then http://localhost:16686 -> service `QuotesApi` -> `GET /api/collections`.

## The note

> The trace for `GET /api/collections` showed the request span containing
> sixteen child database spans: one `SELECT` over `Collections`, then fifteen
> near-identical `SELECT`s against `CollectionItem`, one per collection
> returned. The slow span isn't any single query -- each is sub-millisecond --
> it's their number. `ListByOwnerAsync` loaded the collections, then loaded
> each one's `Items` separately inside a `foreach`, so query count grew
> linearly with rows: a textbook N+1. On local SQLite that is merely visible;
> against a networked database every round trip costs real milliseconds and
> the endpoint degrades as customers add data. I'd fix it by loading `Items`
> in the same query with `.Include(x => x.Items)`, making it one query
> regardless of how many collections come back.

## What made it obvious

Not the duration. Fifteen extra queries against a local file database barely
register in wall-clock time, and no alert would have fired. What gives it away
is the *shape*: a stack of visually identical spans, same SQL, same table,
differing only in a parameter. Once seen, that pattern is unmistakable.

The confirming evidence is that the count tracks the data. Re-run the seeding
script with a different number of collections and the span count moves with
it -- N+1 is precisely that relationship, and demonstrating it changing is
stronger than any single screenshot.

This is also why the exercise's alternative -- `Thread.Sleep(1500)` -- would
have taught less. A sleep appears as an *absence*: a long parent span with
nothing inside explaining it. That demonstrates a limit of automatic
instrumentation rather than its value. The N+1 is a failure this codebase
could plausibly have shipped, and the trace names it precisely.

## Measured

| | Collections | DB spans in trace | Response time |
|---|---|---|---|
| Before | 15 | 16 | _fill in from your run_ |
| After  | 15 | 1  | _fill in from your run_ |

## The fix

`CollectionRepository.ListByOwnerAsync`, before:

```csharp
var collections = await _db.Collections
    .Where(x => x.OwnerId == ownerId)
    .ToListAsync(cancellationToken);

foreach (var collection in collections)          // <- one query per collection
{
    await _db.Entry(collection)
        .Collection(x => x.Items)
        .LoadAsync(cancellationToken);
}
```

after:

```csharp
return await _db.Collections
    .Where(x => x.OwnerId == ownerId)
    .Include(x => x.Items)                       // <- one query, always
    .AsNoTracking()
    .ToListAsync(cancellationToken);
```

`AsNoTracking()` is not required to fix the N+1 -- it is worth adding anyway on
a read-only listing, since the change tracker has no work to do for entities
nobody is going to modify.

## Keeping it fixed

A trace proves the fix today; it does not stop the next person reintroducing
it. `CollectionListingQueryCountTests` counts the SQL commands EF actually
issues and asserts the number does not grow with the row count -- ten
collections and thirty collections must cost the same number of queries. That
is the assertion an N+1 breaks and an ordinary correctness test does not:
every behavioural test in this repository passed against the broken version,
because the endpoint returned exactly the right data. It just asked sixteen
times.

## Finding this in production (KQL)

Slowest endpoints over the last hour, which is where a latency investigation
starts:

```kusto
requests
| where timestamp > ago(1h)
| summarize
    count(),
    avg(duration),
    p95 = percentile(duration, 95),
    p99 = percentile(duration, 99)
  by name
| order by p99 desc
```

Requests making an unusual number of database calls -- the N+1 signature
itself, rather than its symptom:

```kusto
dependencies
| where timestamp > ago(1h) and type in ("SQL", "sqlite")
| summarize dbCalls = count(), dbTime = sum(duration) by operation_Id
| where dbCalls > 10
| join kind=inner (requests | project operation_Id, name, duration) on operation_Id
| project name, dbCalls, dbTime, requestDuration = duration, operation_Id
| order by dbCalls desc
```

The second query is the more useful of the two. Sorting by duration finds
endpoints that are slow *now*; grouping by database calls per request finds
the ones that will become slow as data grows -- while they are still fast
enough that nobody has complained.
