# Day 5, Task 7 — Mentor Submission (Polly resilience on outbound HTTP)

Retry with jittered exponential backoff, a circuit breaker, and timeouts around this API's outbound HTTP calls, using `Microsoft.Extensions.Http.Resilience` (Polly v8 underneath).

## GitHub Link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day-5-task-7/Day5/piece2

---

## First: which call is "the HTTP call"?

The exercise says *"If your API calls any other API (Entra ID for token validation, an external service)"*. It is worth being precise about what this API actually calls, because the answer is not what a `grep` for `HttpClient` suggests — that returns nothing in application code.

| Dependency | Over HTTP? | Owned by |
| --- | --- | --- |
| SQLite via EF Core | No — in-process file access | — |
| Application Insights | Yes | The Azure Monitor exporter, which has its own transmission/retry pipeline and does not go through `IHttpClientFactory` |
| **Entra ID (`AzureAd:Authority`)** | **Yes** | **The `EntraId` JwtBearer handler** |

So there is exactly one call worth wrapping, and no line of this codebase issues it: the `EntraId` scheme fetches Entra's OpenID Connect metadata document and its JSON Web Key Set from `login.microsoftonline.com`, and refreshes them periodically. Those keys are what token signatures are checked against — when that fetch fails, **every Entra-issued token in flight fails to validate**, which surfaces to callers as 401s rather than as a dependency error.

Left alone, ASP.NET Core builds a plain `HttpClient` for that fetch: no retry, no circuit breaker, one 60-second timeout.

## What was added

`QuotesApi/Extensions/ResilienceExtensions.cs` registers a named client and its pipeline; `InfrastructureExtensions` hands that client to the JwtBearer handler:

```csharp
services.AddHttpClient("entra-id", client => client.Timeout = Timeout.InfiniteTimeSpan)
        .AddResilienceHandler("default", (builder, context) => { /* … */ });

services.AddOptions<JwtBearerOptions>("EntraId")
        .Configure<IHttpClientFactory>((options, factory) =>
            options.Backchannel = factory.CreateClient("entra-id"));
```

Two decisions in those six lines that are easy to get wrong:

- **`Timeout.InfiniteTimeSpan` on the HttpClient.** This is not "no timeout" — it hands timeout control to Polly. `HttpClient.Timeout` is a single cap that throws `TaskCanceledException` without distinguishing "this attempt timed out" from "the caller cancelled", and if it fires first it makes the per-attempt timeout below unexpressible. The real ceiling is the 10-second total timeout in the pipeline.
- **Named options (`AddOptions<JwtBearerOptions>("EntraId")`), not the unnamed default.** `JwtBearerOptions` is registered per scheme. Configuring the unnamed instance compiles, runs, and silently does nothing to the `EntraId` scheme — the handler keeps its own bare client and the whole exercise becomes decorative.

## The pipeline, outermost first

Strategies added first sit **outside** those added after, so the order in code *is* the policy:

```
total timeout (10s)
  └─ retry (3 attempts, exponential + jitter)
       └─ circuit breaker (50% / 30s)
            └─ attempt timeout (3s)
                 └─ the request
```

| Strategy | Setting | Why this value |
| --- | --- | --- |
| Total timeout | 10s | The promise to the caller. Covers every attempt *and* every backoff delay between them, so an inbound request waiting on token validation is never held longer than this. |
| Retry | 3 attempts, exponential, `UseJitter = true`, base 1s | Jitter is the part that matters: without it every replica that saw the same outage retries at the same instants, and the recovering service takes a synchronized wave instead of spread traffic. |
| Retry predicate | left at default | Default handles 5xx, 408, `HttpRequestException`, and inner timeouts — and does **not** retry 4xx. Retrying a 401 or 404 is three more ways to get the same answer. |
| Circuit breaker | `FailureRatio 0.5`, `SamplingDuration 30s`, `MinimumThroughput 10`, `BreakDuration 15s` | `MinimumThroughput` is what makes a ratio meaningful — without it, one failure out of one call is a 100% failure rate and the breaker opens on a single blip. |
| Attempt timeout | 3s | Deliberately **not** 10s. A per-attempt timeout equal to the total budget lets the first attempt consume everything and leaves no room for the retries above to run at all. |

Two orderings this specifically avoids:

- **Timeout innermost only.** Caps each attempt but never the operation — three retries with backoff can run well past ten seconds while the caller waits.
- **Circuit breaker outside the retry.** A burst of retries against one dead host would count as a single failure rather than several, and the breaker would take far longer to notice a dependency is down.

## "Log every retry; never silently swallow failures"

Every retry logs a **warning** (not information — this is a dependency misbehaving, not routine traffic) carrying the attempt number, the backoff delay, and the outcome — status code or exception message:

```
Entra ID metadata request failed, retrying. Attempt 2 of 3, waiting 00:00:01.9, outcome: ServiceUnavailable
```

The breaker logs its own transitions: `OnOpened` at **error** level (Entra-issued tokens cannot be validated while it is open — that is an outage, and it should page someone), `OnHalfOpened` and `OnClosed` at information.

The failure mode this guards against is subtle: a retry that nobody records is an outage that shows up **only as latency**. The call eventually succeeds, the dashboard stays green, and the fact that the dependency needed three attempts is lost. Since these logs flow through Serilog into the OpenTelemetry pipeline from Task 5, they land in Application Insights alongside the request telemetry, where `traces | where message has "retrying"` finds them.

## Tests

`Quotes.Tests.Unit/ResilienceExtensionsTests.cs` — three tests, primary handler stubbed so nothing touches the network:

| Test | Asserts |
| --- | --- |
| `WhenTransientFailure_RetriesAndSucceeds` | 503 then 200 → final status OK **and the handler was called twice**. Asserting the count is what distinguishes "the retry worked" from "the first call happened to succeed". |
| `WhenRetrying_LogsAWarningForEveryAttempt` | Two 503s → exactly two warnings, each naming `ServiceUnavailable` |
| `WhenClientError_DoesNotRetry` | 404 → one call only, no retry log |

Deliberately not tested: the breaker opening (needs 10 failing calls inside a 30-second window — seconds of runtime for a configured constant, not logic) and the total timeout (would mean sleeping past ten seconds). Both are called out here rather than left as silent gaps.

## Verifying by hand

```powershell
cd Day5\piece2
dotnet test Quotes.Tests.Unit
```

To watch it against a real failure, point the authority at a black hole and call an endpoint with an `api://`-shaped audience so the `EntraId` scheme is selected (see `AuthSchemeSelector`):

```powershell
$env:AzureAd__Authority="https://login.microsoftonline.test/blackhole/v2.0"
dotnet run --project QuotesApi
```

The console shows three retry warnings with growing, jittered delays, then a failure — and after ten calls in thirty seconds, the breaker opening. What it will **not** show is a request hanging for sixty seconds, which is what the same call did before this change.

---

## What did you learn this session?

- **Order is the policy, not a style choice.** The same four strategies in a different sequence give a different system: timeout innermost only means the ten-second promise is not kept; breaker outside retry means a burst of retries against a dead host reads as one failure and the breaker barely notices. The code reads outermost-first, and that is the only way to review it.
- **Setting `HttpClient.Timeout = InfiniteTimeSpan` is how you turn timeouts *on*.** It looks like removing a safety net; it is handing the net to something that can tell "this attempt hung" apart from "the caller went away", which `HttpClient.Timeout` cannot.
- **The interesting HTTP call had no `HttpClient` in the codebase.** Framework-owned dependencies — an auth handler's backchannel, an exporter's transmitter — are exactly the calls that are easy to leave unprotected, because grepping for the client type finds nothing.

## What would break this?

- **Configuring the unnamed `JwtBearerOptions` instead of the `"EntraId"` named one.** Compiles, runs, changes nothing; the handler quietly keeps its own unprotected client.
- **Setting the attempt timeout equal to the total timeout.** The first attempt eats the entire budget and the retries never get to run — the pipeline looks configured and behaves like a single try.
- **Dropping `MinimumThroughput`.** One failure out of one call is a 100% failure rate, so the breaker opens on the first blip and takes Entra authentication down for its whole break duration.
- **Widening `ShouldHandle` to all failures.** Retrying 401/403 turns a clear, immediate answer into three delayed identical answers, and against a rate limiter it makes the problem worse rather than better.
- **A second outbound dependency added later without going through `IHttpClientFactory`.** Nothing in the build fails; the new call simply has none of this, exactly as the Entra backchannel did before this change.
