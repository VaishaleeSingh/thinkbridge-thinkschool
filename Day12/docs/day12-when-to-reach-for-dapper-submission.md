# Day 12 — mentor submission (when to reach for Dapper)

## GitHub link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day12-when-to-reach-for-dapper/Day12

(Replace with the pull request URL once opened.)

## The headline: there is a crossover, and I measured both sides of it

I reimplemented the hot read path with Dapper and it **lost to EF by 18%**. So
I built a second experiment at the other end of the range, and there Dapper
**won by 25%**. Same table, same provider, same load, same harness.

| Experiment | Result set | EF Core | Dapper | Winner |
|---|---|---|---|---|
| **A** — `GROUP BY` author counts | 500 rows × 2 narrow cols | **460.9 req/s** | 392.3 req/s | **EF, +17.5%** |
| **B** — wide row projection | 5,000 rows × 4 cols (`Text` ≈ 620 chars) | 519.0 req/s | **646.4 req/s** | **Dapper, +24.6%** |

Neither number alone is the answer. The pair is, because it locates the line
rather than picking a side — and the line turns out not to be where the task's
framing ("hot read paths") would put it.

## What this task asks for, in simple words

EF Core writes your SQL, tracks changes, and lets you query in C#. **Dapper**
does one job: you hand it SQL and a type, it turns result rows into objects. No
query building, no change tracking, no model.

So Dapper is not "the fast one" in general — it is faster *at the one job it
does*, because it skips machinery EF has to run. The question is whether that
skipped machinery is a measurable share of a real request. And then: what rule
stops a teammate rewriting the whole app in Dapper because they read a
benchmark once.

## Implementation plan (written before the code)

1. **Pick the read path that is genuinely hot**, not a contrived one:
   `/api/diagnostics/authors-quotes-grouped`, the query Day 11 load-tested and
   optimised. Reusing a query with known numbers inherits a known-good harness.
2. **Make the comparison fair in two specific ways** (both easy to get wrong in
   a way that flatters Dapper) — see below.
3. **Same load, same session**: `bombardier -c 20 -d 30s`, both endpoints back
   to back, so nothing environmental differs.
4. **Compare the SQL as well as the timing** — if the SQL differs, the timing
   measures SQL quality, not the data-access layer.
5. **Write the rule from the measurement**, whichever way it falls.
6. *(Added after experiment A came out against Dapper.)* **Measure the other end
   too.** A rule derived from one data point is a guess with a number attached.

## The two fairness decisions

These are the difference between a measurement and a sales pitch.

**Dapper borrows EF's connection**, via `db.Database.GetDbConnection()`:

```csharp
var connection = db.Database.GetDbConnection();
var results = (await connection.QueryAsync<AuthorQuoteRow>(
    new CommandDefinition(sql, cancellationToken: cancellationToken)))
    .AsList();
```

Opening its own `SqliteConnection` would have measured connection setup as well
as mapping, and sidestepped EF's pooling — part of any "win" would have been the
harness. Sharing the connection means the only thing differing between the two
endpoints is how a result set becomes objects.

**The SQL is written to match what EF generates, not to beat it.**

```sql
-- EF Core generates (captured from the API's own log on Day 11):
SELECT [q].[Author], COUNT(*) FROM [Quotes] AS [q] GROUP BY [q].[Author];

-- Dapper, hand-written to be the same query:
SELECT Author, COUNT(*) AS QuoteCount
FROM Quotes
GROUP BY Author
```

**These are the same query.** Hand-writing a *smarter* one would have proved
that better SQL is faster — true, but not the claim under test.

## Experiment A — the hot path. EF won.

500 rows, two narrow columns, `GROUP BY` over a 100,000-row table.

```
EF      Reqs/sec 460.94    p50 42.10ms   p99 60.13ms   2xx 13,833
Dapper  Reqs/sec 392.33    p50 50.09ms   p99 67.02ms   2xx 11,775
```

Reproducible — an earlier run gave EF 475.8 / Dapper 397.6, the same ~18–20%
gap. The result is stable, not noise.

## Experiment B — the wide result set. Dapper won.

5,000 rows, four columns, `Text` ≈ 620 characters each (~1.1 MB of strings per
request). Both endpoints project into the **same** DTO and return only a
summary — a count and the total text length.

```
EF      Reqs/sec 519.01    p50 36.40ms   p99 85.63ms   2xx 15,546
Dapper  Reqs/sec 646.42    p50 27.81ms   p99 69.26ms   2xx 19,416
```

Dapper: **+24.6% throughput, 23.6% lower p50, 19.1% lower p99.**

Returning a summary rather than the rows is what makes this measure mapping.
Serialising 5,000 wide rows to JSON would dominate the request and is identical
work for both sides — excluding it isolates the thing under test. Summing the
text lengths forces every row to be fully materialized rather than lazily
skipped. And both sides materializing the same `QuoteWideRow` means neither
gains from a different object shape.

**The integrity check that makes this comparable:** both endpoints reported
`totalTextLength: 1108890`, identical to the digit. Same rows, same data, same
work. If those had differed, nothing else in the table would have meant anything.

## Why the crossover happens — and why "large result set" is the wrong rule

The tempting summary is "Dapper wins on big result sets". The measurements say
something more precise, and the giveaway is buried in the throughput numbers:

**Experiment B is *faster* than experiment A for both libraries** (EF: 519 vs
461 req/s). That looks backwards — 5,000 wide rows should cost more than 500
narrow ones. It isn't backwards, because the two experiments do very different
amounts of *database* work:

| | database work | mapping work |
|---|---|---|
| A (`GROUP BY`) | scan + aggregate **100,000 rows** — expensive | 500 narrow rows — trivial |
| B (`ORDER BY Id LIMIT 5000`) | PK-ordered read of **5,000 rows** — cheap | 5,000 wide rows — substantial |

In A the query dominates (~40 ms of the ~42 ms p50), so the mapper is a rounding
error and EF's amortised overhead edges it. In B the query is cheap and the
mapper is doing real work, so Dapper's leaner materialization shows through.

So the rule is not about result-set size. **It is about the ratio of mapping
work to query work.** A query that reads a million rows and returns one number
will never benefit from Dapper, however "hot" it is. A query that reads few rows
and returns many wide ones might. That distinction is invisible if you only
measure one end, which is exactly why experiment A alone would have produced a
wrong rule.

## Two bugs of mine, both instructive

### 1. The positional record that 400'd every request

First version used `record AuthorQuoteRow(string Author, int QuoteCount)`. It
compiled cleanly, and then **every single request failed** — 14,632 of them,
all 4xx:

```
A parameterless default constructor or one matching signature
(System.String Author, System.Int64 QuoteCount) is required for
QuotesApi.Extensions.AuthorQuoteRow materialization
```

SQLite returns `COUNT(*)` as **Int64**. Dapper resolves a constructor by
reflection and matches parameter types *exactly* — it will not narrow Int64 to
Int32 to make a constructor fit. EF never hits this because it is handed a
model, knows the store type, and compiles a materializer that converts.

The obvious fix — declare `long QuoteCount` — trades one bug for a worse one:
on **SQL Server** `COUNT(*)` is Int32, and Dapper will not widen Int32 to Int64
for a constructor parameter either. A DTO tuned to the dev database would break
against the production provider, at runtime, on the hot path.

The correct fix is a class with settable properties, because Dapper's property
path *does* coerce:

```csharp
public sealed class AuthorQuoteRow
{
    public string Author { get; set; } = "";
    public int QuoteCount { get; set; }
}
```

That asymmetry — **constructor mapping is strict, property mapping coerces** —
is why most Dapper code in the wild uses mutable DTOs rather than records. Not
style; the mapper's actual contract.

### 2. The extra copy that was penalising Dapper

The first working version ended `.ToList()`. But `QueryAsync<T>` already buffers
into a `List<T>` internally and returns it as `IEnumerable<T>`, so `.ToList()`
copied all 500 rows a **second** time on every request — while EF's
`ToListAsync()` returns its list with no copy. Dapper ships `AsList()` for
exactly this.

I found it re-reading my own code before writing the conclusion, not because a
test failed. I was about to report "Dapper is 20% slower" from a harness doing
500 extra allocations per request on the Dapper side only.

**Fixing it barely moved the number** (397.6 → 392.3 req/s), so the flaw was not
the cause — but the result is only trustworthy because it was fixed and re-run
rather than explained away.

## Both endpoints, live

Experiment A, identical answers — 500 authors, 200 quotes each, one query each:

![Browser showing the EF endpoint response: strategy single GROUP BY, queriesIssued 1, elapsedMs 27, authorCount 500](images/d01-api-ef-grouped.jpg)

![Browser showing the Dapper endpoint response: strategy single GROUP BY via Dapper, queriesIssued 1, elapsedMs 25, authorCount 500](images/d02-api-dapper-grouped.jpg)

## The rule I would give a teammate

> **EF Core is the default. Reach for Dapper when the mapping is the work — not
> when the endpoint is merely hot.**
>
> The test I would actually apply, in order:
>
> 1. **Is this endpoint measurably hot?** Name its p99 under load. If you
>    cannot, you are not ready to optimise it.
> 2. **Where does the time go — the query or the mapping?** This is the
>    question, and the one people skip. Look at the generated SQL and the row
>    count. A query that reads a lot and returns a little (aggregates, counts,
>    `GROUP BY`) is query-bound: Dapper cannot help, and we measured it losing
>    18% on exactly that shape. A query that reads a little and returns many
>    wide rows is mapping-bound: we measured Dapper winning 25% there.
> 3. **Can you write better SQL than EF did?** If EF already generated the query
>    you would have written — as it had in experiment A — there is no SQL
>    advantage on the table, only a mapping one.
> 4. **Re-measure after, and keep the number.** A rewrite that is not
>    re-measured is a guess with extra steps. Experiment A came out *worse*;
>    without the second measurement we would have shipped a regression and
>    called it an optimisation.
>
> **What you take on when you do:** you now own the SQL and the type mapping.
> The 400-on-every-request bug above is the shape of that cost — a plausible DTO
> that compiles and fails at runtime, whose obvious fix breaks on a different
> provider. EF was doing that work silently and correctly.
>
> **So, concretely:** exports and reporting queries that stream thousands of
> wide rows — yes, that is mapping-bound and Dapper earns it. Dashboard counts
> and aggregates, however hot — no, those are query-bound; fix the SQL or the
> index instead (which is what Day 11 did, for a 2,592× win that no ORM choice
> would have touched).

## What did you learn this session?

That "X is faster than Y" is almost never a property of X and Y. My first
measurement said EF beat Dapper by 18% and I could have stopped there with a
tidy, confident, **wrong** rule. The second experiment reversed the result, and
the useful output was not "Dapper won after all" but the *mechanism*: the
crossover is driven by the ratio of mapping work to query work, which is why
experiment B was paradoxically faster than A for both libraries despite handling
ten times the rows.

The tell was in a number I nearly skimmed past. Experiment B out-throughputting
experiment A made no sense under "wide is slower", and chasing that oddity is
what produced the actual rule. A result that contradicts your framing is worth
more attention than one that confirms it.

Second thing, about process: I nearly published a benchmark with an extra
500-element copy on the Dapper side, and I nearly published a rule extrapolated
from a single data point. Both would have looked completely reasonable in
writing. The fix in both cases was cheap — re-read the code, run the other end —
and I only did it because I had written down what I could *not* yet claim.

## What would break this?

- **One provider, one machine.** SQLite, on a laptop running the API, the
  database and the load generator together. Against SQL Server over a network,
  latency becomes a bigger share of every request and would compress both gaps —
  the crossover point would move, though the mechanism should not.
- **The crossover point itself is not calibrated.** I measured two points (500
  narrow rows, 5,000 wide rows) and found opposite winners. I did *not* find
  where they cross. "Mapping-bound vs query-bound" is the right axis and it is
  now evidenced at both ends, but if a teammate asks "is 800 medium rows
  mapping-bound?", the honest answer is measure it, not consult this table.
- **The row count drifted from plan.** The seed ran on top of an already-seeded
  table, so `Quotes` held **100,000 rows / 200 per author**, not the 50,000 /
  100 earlier days used. Both endpoints in each experiment saw identical data,
  so each A/B holds, but absolute numbers are not comparable to Day 11's.
- **`elapsedMs` in the responses is not a benchmark.** Single samples, and they
  visibly disagree with the load tests — the wide EF endpoint reported 173 ms
  once and 15 ms once, same code, same data. Only the 30-second runs are stable.
  Quoting a single-request number in either direction would be cherry-picking.
- **Experiment B is deliberately not a realistic endpoint.** It materializes
  5,000 wide rows and then throws them away, returning a summary. That is what
  isolates mapping cost, and it is also why it is not evidence about a real
  endpoint that would have to *serialize* those rows — where JSON would dominate
  and both libraries would look much closer.
- **Two data-access libraries is a real ongoing cost.** Dapper is now a
  dependency and there are two ways to query in one codebase; every future
  reader learns both, and the boundary is maintained by convention alone. This
  branch therefore keeps the Dapper endpoints as documented comparisons rather
  than replacing the EF ones — the measurement justifies Dapper for a shape we
  do not currently ship.
