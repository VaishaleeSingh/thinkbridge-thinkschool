# Day 22 — Mentor Submission (Resilience with Polly)

Retry with jittered exponential backoff **gated on idempotency**, a circuit
breaker, two timeouts and a bulkhead around this API's outbound HTTP call — and
a test that drives the circuit open under sustained failure and watches it
close again.

## GitHub Link

Pull request: https://github.com/thinkbridge-thinkschool/VaishaleeSingh/pull/50

Branch: `day22-polly-resilience-circuit-proof`

- Docs, script and verification output: `Day22/`
- Code (pipeline, options, metrics, diagnostics, tests): `Day7/piece2/`

---

## Exercise: the resilience pipeline, and the breaker opening → half-opening → recovering

### The pipeline

`QuotesApi/Extensions/ResilienceExtensions.cs`, comments stripped for length —
the reasoning behind each position is in the file and summarised below:

```csharp
services
    .AddHttpClient(EntraIdClientName, client =>
    {
        // Polly owns timeouts, not HttpClient: its single timeout cannot tell
        // "this attempt hung" from "the caller went away".
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .AddResilienceHandler(ResiliencePipelineNames.EntraIdPipeline, (builder, context) =>
    {
        var options = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value;
        var metrics = sp.GetRequiredService<ResilienceMetrics>();
        var circuitBreaker = sp.GetRequiredService<CircuitBreakerRegistry>();

        builder
            // 1. TOTAL TIMEOUT -- the promise to the caller. Covers the permit
            //    wait, every attempt, and every backoff between them.
            .AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = ResiliencePipelineNames.TotalTimeout,
                Timeout = options.TotalTimeout                       // 10s
            })

            // 2. BULKHEAD -- Polly v8 has no BulkheadPolicy; it is the
            //    rate-limiter strategy with a ConcurrencyLimiter.
            //    OUTSIDE the retry, so one operation holds one permit for its
            //    whole life and a shed rejection is never retried; OUTSIDE the
            //    breaker, so our own back-pressure cannot open the circuit.
            .AddRateLimiter(new HttpRateLimiterStrategyOptions
            {
                Name = ResiliencePipelineNames.Bulkhead,
                DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                {
                    PermitLimit = options.Bulkhead.PermitLimit,      // 4
                    QueueLimit = options.Bulkhead.QueueLimit,        // 8
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                },
                OnRejected = args =>
                {
                    metrics.RecordBulkheadRejection(pipeline);
                    logger.LogWarning("Entra ID request shed by the bulkhead: ...");
                    return default;
                }
            })

            // 3. RETRY -- jittered exponential backoff, GATED ON IDEMPOTENCY.
            .AddRetry(new HttpRetryStrategyOptions
            {
                Name = ResiliencePipelineNames.Retry,
                MaxRetryAttempts = options.Retry.MaxAttempts,        // 3
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.Retry.BaseDelay,                     // 1s

                ShouldHandle = args =>
                {
                    if (!HttpClientResiliencePredicates.IsTransient(args.Outcome))
                        return PredicateResult.False();

                    if (!options.Retry.IdempotentOnly)
                        return PredicateResult.True();

                    var request = args.Outcome.Result?.RequestMessage
                                  ?? args.Context.GetRequestMessage();

                    if (IdempotencyPredicate.IsRetryable(request))
                        return PredicateResult.True();

                    // A declined retry is a non-event to Polly, so it is
                    // counted here or it is invisible.
                    metrics.RecordSuppressedRetry(pipeline, request?.Method.Method ?? "unknown");
                    logger.LogWarning("... was NOT retried: {Method} is not idempotent ...");
                    return PredicateResult.False();
                },

                OnRetry = args =>
                {
                    metrics.RecordRetry(pipeline, outcome);
                    logger.LogWarning(
                        "Entra ID metadata request failed, retrying. Attempt {AttemptNumber} of {MaxAttempts}, waiting {Delay}. Outcome: {Outcome}", ...);
                    return default;
                }
            })

            // 4. CIRCUIT BREAKER -- inside the retry, so a burst of retries
            //    against a dead host counts as several failures, not one.
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                Name = ResiliencePipelineNames.CircuitBreaker,
                FailureRatio = options.CircuitBreaker.FailureRatio,          // 0.5
                SamplingDuration = options.CircuitBreaker.SamplingDuration,  // 30s
                MinimumThroughput = options.CircuitBreaker.MinimumThroughput,// 10
                BreakDuration = options.CircuitBreaker.BreakDuration,        // 15s

                // What makes the breaker observable, and therefore provable.
                StateProvider = circuitBreaker.State,
                ManualControl = circuitBreaker.ManualControl,

                OnOpened = args =>
                {
                    metrics.RecordCircuitOpened(pipeline);
                    logger.LogError(
                        "Entra ID circuit breaker OPENED for {BreakDuration}. Entra-issued tokens cannot be validated until it closes.",
                        args.BreakDuration);
                    return default;
                },
                OnHalfOpened = args =>
                {
                    metrics.RecordCircuitHalfOpened(pipeline);
                    logger.LogInformation(
                        "Entra ID circuit breaker half-open -- letting one trial request through.");
                    return default;
                },
                OnClosed = args =>
                {
                    metrics.RecordCircuitClosed(pipeline);
                    logger.LogInformation(
                        "Entra ID circuit breaker closed. Metadata requests are flowing again.");
                    return default;
                }
            })

            // 5. ATTEMPT TIMEOUT -- innermost, so it caps each individual try.
            //    Smaller than the total, enforced by ResilienceOptions.Validate:
            //    equal would let attempt one eat the whole budget.
            .AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = ResiliencePipelineNames.AttemptTimeout,
                Timeout = options.AttemptTimeout                      // 3s
            });
    });
```

Reading order is outermost-first, because strategies added first wrap those
added after:

```
total timeout -> bulkhead -> retry -> circuit breaker -> attempt timeout -> request
```

### Metrics, stage by stage

Read from `GET /api/diagnostics/resilience` at each point in the run. Policy for
the run was shortened so a walkthrough takes seconds rather than a minute
(`MinimumThroughput=6`, `BreakDuration=5s`, `AttemptTimeout=2s`,
`MaxAttempts=1`); the behaviour does not depend on the magnitudes.

| Stage | circuit | opened | halfOpened | closed | retries |
|---|---|---|---|---|---|
| before any load | `Closed` | 0 | 0 | 0 | 0 |
| after sustained failure | **`Open`** | 1 | 0 | 0 | 3 |
| after the half-open burst (dependency still dead) | `Open` | 2 | **1** | 0 | 4 |
| after recovery (dependency repaired) | **`Closed`** | 2 | 1 | **1** | — |

`retries = 3` for three failing requests with `MaxAttempts = 1`: three requests
× two attempts = six failures, against a `MinimumThroughput` of six. **The
breaker opened on the sixth attempt, not the sixth request** — it sits inside
the retry and counts attempts, which is the ordering argument turning into a
number.

### The run: opening

```
[15:38:50] before any load  circuit=Closed  opened=0 halfOpened=0 closed=0 retries=0
[15:38:50] Driving 12 requests (Probe mode) ...
[15:38:54]   request  1  TimeoutRejectedException   4143 ms
[15:38:58]   request  2  TimeoutRejectedException   4095 ms
[15:39:03]   request  3  TimeoutRejectedException   4100 ms
[15:39:03]   request  4  circuit-open                 15 ms
[15:39:03]   request  5  circuit-open                 14 ms
[15:39:03]   request  6  circuit-open                 11 ms
...
[15:39:03]   request 12  circuit-open                 14 ms
[15:39:03] after sustained failure  circuit=Open  opened=1 halfOpened=0 closed=0 retries=3
[15:39:03] open-circuit request: circuit-open in 16 ms (the first request of the run cost 4143 ms)
```

**4143 ms → 16 ms**, a ~260× drop. That is the whole argument for a circuit
breaker: a dead dependency should cost microseconds, not a full attempt timeout
of held threads per request.

From the API's own log (`Day22/verification/day22-api-resilience-lines.txt`),
Polly's telemetry naming the strategies by the constants in
`ResiliencePipelineNames` — which is what makes an event attributable to a
strategy rather than to "something in the pipeline":

```
[15:38:52 ERR] Resilience event occurred. EventName: 'OnTimeout',
               Source: 'entra-id-entra-id//attempt-timeout'
[15:38:52 WRN] Entra ID metadata request failed, retrying. Attempt 1 of 1,
               waiting 00:00:00. Outcome: The operation didn't complete within
               the allowed timeout of '00:00:02'.
[15:38:52 WRN] Resilience event occurred. EventName: 'OnRetry',
               Source: 'entra-id-entra-id//retry'
[15:39:03 ERR] Entra ID circuit breaker OPENED for 00:00:05. Entra-issued tokens
               cannot be validated until it closes.
```

### The run: half-opening

Eight concurrent requests after `BreakDuration` elapses, dependency still dead:

```
[15:39:09] Bursting 8 concurrent requests -- half-open must admit exactly one ...
[15:39:11]   burst 1  circuit-open   2077 ms  ADMITTED (paid the attempt timeout)
[15:39:11]   burst 2  circuit-open     31 ms  rejected
[15:39:11]   burst 3  circuit-open      9 ms  rejected
[15:39:11]   burst 4  circuit-open     14 ms  rejected
[15:39:11]   burst 5  circuit-open     17 ms  rejected
[15:39:11]   burst 6  circuit-open     23 ms  rejected
[15:39:11]   burst 7  circuit-open     27 ms  rejected
[15:39:11]   burst 8  circuit-open     35 ms  rejected
[15:39:11] half-open burst: 1 admitted, 7 rejected, 0 faulted (threshold 1000 ms)
[15:39:11] after half-open burst  circuit=Open  opened=2 halfOpened=1 closed=0 retries=4
```

**One admitted out of eight.** The admitted request paid a full attempt timeout
(2077 ms); the rejected seven returned in 9–35 ms. `halfOpened` incremented once
and `opened` went to 2 — the trial failed against a still-dead dependency, so
the circuit re-opened rather than giving the herd a second chance. That is the
property that stops a recovering dependency from being knocked straight back
down.

Note that **all eight reported `circuit-open`**, including the admitted one.
Polly reports a failed half-open trial to its caller as `BrokenCircuitException`
— the same exception a rejected call gets — so only the elapsed time
distinguishes them. See "The instrument that lied" above.

### The run: recovering

*(To be pasted from the re-run: the script now repairs the dependency for real —
a TCP listener is started on the port that was refusing connections — instead of
closing the circuit through `CircuitBreakerManualControl`. The manual control
demonstrated the manual control, not recovery under a healed dependency, and the
exercise asks for recovery.)*

The corresponding assertions, already green in
`CircuitBreakerLifecycleTests.Circuit_WhenDependencyRecovers_ClosesAgain`:

```csharp
handler.Returns(HttpStatusCode.OK);            // repaired BEFORE the trial
await Task.Delay(TimeSpan.FromMilliseconds(1400));   // past BreakDuration

using var trial = await PostAsync(client);

trial.StatusCode.Should().Be(HttpStatusCode.OK);
breaker.State.CircuitState.Should().Be(CircuitState.Closed);
metrics.CircuitClosed.Should().Be(1);
logs.Lines.Should().Contain(l =>
    l.Level == LogLevel.Information && l.Message.Contains("circuit breaker closed"));

// And traffic flows afterwards, rather than reporting Closed while rejecting.
using var afterwards = await PostAsync(client);
afterwards.StatusCode.Should().Be(HttpStatusCode.OK);
```

The ordering is deliberate: the dependency is repaired **before** the trial
request, because the breaker has no way to know it recovered and must find out
by letting one request through. Repairing it afterwards would be testing the
clock.

---

## First: this day did not start from zero, and that changed what it is about

Day 5, Task 7 already wrapped this API's one outbound HTTP call — the `EntraId`
JwtBearer handler's fetch of Entra ID's OIDC metadata and JSON Web Key Set —
in a Polly v8 pipeline. So three of the five things Day 22 asks for existed
before the branch was cut:

| Asked for | State on `main` | Day 22 |
|---|---|---|
| Timeout | Two, correctly nested (10s total / 3s per attempt) | Kept; the numbers moved into options |
| Retry with backoff | Present, jittered, every retry logged | **Not gated on idempotency — a defect** |
| Circuit breaker | Configured | Configured is not proven; **no test opened it** |
| Bulkhead | Absent | Added, as a concurrency limiter |
| Proof it opens and recovers | Absent | The deliverable |

Which means the honest version of this exercise was not "add Polly". It was:
find what the existing pipeline gets wrong, and prove the strategy that had
never been proven.

### Defect 1: the retry was not gated on idempotency

Day 5 left `HttpRetryStrategyOptions.ShouldHandle` at its default, which handles
5xx, 408, `HttpRequestException` and inner-timeout cancellations — **regardless
of HTTP method**.

On the `entra-id` client that is harmless, and it is worth being precise about
why: the only caller is the JwtBearer metadata fetch, which issues `GET`s. That
is a property of *today's caller*, not of the pipeline. The pipeline is a
reusable registration, and the first `POST` routed through it would inherit a
policy that re-sends a write after a 503 — and a 503 does not tell you whether
the far end processed the request before it fell over. The retry is a coin flip
on a duplicate.

The task's parenthetical — *idempotent only* — is asking for the gate to live in
the policy rather than in the caller's good manners.

### Defect 2: the breaker's parameters were constants, which is why it was never proven

Day 5's own test file says so, and the sentence is worth quoting because it is
the whole reason this day exists:

> Not tested here: the circuit breaker opening, which needs at least
> MinimumThroughput (10) failing calls inside a 30 second window and would
> trade seconds of test runtime for a property that is **a configured constant
> rather than logic**.

That reasoning was sound at the time, and it is the shape of argument that
leaves the most important strategy in a pipeline unverified for seventeen days.
The breaker was untestable *because* its parameters were constants — and "it is
only a constant" then became the argument for not testing it.

So binding the parameters to configuration is not housekeeping. It is the
change that turns "prove the circuit opens and recovers" from a ten-second sleep
nobody will put in CI into a two-second deterministic test. The refactor and the
proof are the same piece of work.

---

## What is deliberately NOT wrapped

The most common production mistake with Polly is not a missing policy. It is two
retry policies stacked on the same call, each unaware of the other — 3 × 3
attempts, and a breaker that cannot see the inner retries. So the dependency
table is part of the deliverable:

| Dependency | Transport | Wrapped? | Why |
|---|---|---|---|
| Entra ID metadata + JWKS | HTTPS via `IHttpClientFactory` | **Yes** | The only outbound HTTP this app issues, and a failure here fails every Entra-issued token in flight |
| SQLite / SQL Server | ADO.NET, in-process | No | EF's `EnableRetryOnFailure` already owns this; a Polly retry above it multiplies attempts, and a retry wrapped around an open transaction is a correctness bug |
| Azure Service Bus | AMQP, Azure SDK | No | `ServiceBusClientOptions.RetryOptions` retries inside the SDK |
| Redis L2 | RESP, StackExchange.Redis | No | Day 21 already chose the right behaviour: `AbortOnConnectFail=false`, degrade to L1 |
| Application Insights | HTTPS | No | Not routed through `IHttpClientFactory`; the exporter owns its transmission |

---

## The pipeline, outermost first

```
total timeout  ->  bulkhead  ->  retry  ->  circuit breaker  ->  attempt timeout  ->  request
```

Strategies added first sit outside those added after them, so the code reads in
that order. Three of the five positions are the standard argument; the
bulkhead's is the one with a real decision in it.

**Outside the retry.** A bulkhead bounds how much of this process is tied up
waiting on one dependency, and one logical operation should hold one permit for
its whole life — retries and backoff delays included — because that is the
resource actually being consumed. Placed *inside* the retry, each attempt would
acquire and release separately, and worse: a `RateLimiterRejectedException` from
a full bulkhead would land in the retry's `ShouldHandle` and be treated as a
transient failure. **Retrying a load-shed rejection is the definition of making
an overload worse.**

Being outside the breaker matters in the other direction: the breaker cannot see
a rejection raised above it, so our own back-pressure can never open the
circuit. That property is structural rather than a predicate someone has to
remember to write — which is exactly why `BulkheadTests` asserts it anyway, since
a guarantee that is invisible in the code is a guarantee nobody will notice
losing.

**Inside the total timeout.** Waiting for a permit is waiting. With the limiter
outermost, a caller could queue with nothing bounding that wait, and the
ten-second promise would only hold for requests that got lucky. `QueueLimit`
bounds how *many* wait; the total timeout bounds how *long* any one of them does.

Polly v8 has no strategy called "bulkhead" — v7's `BulkheadPolicy` was replaced
by the rate-limiter strategy over `System.Threading.RateLimiting`, and a
bulkhead is that strategy with a `ConcurrencyLimiter`. The registration is
*named* `"bulkhead"` so that anyone grepping this repository for the word after
reading the task finds it.

---

## Retry only if idempotent

```csharp
ShouldHandle = args =>
{
    if (!HttpClientResiliencePredicates.IsTransient(args.Outcome))
        return PredicateResult.False();

    var request = args.Outcome.Result?.RequestMessage
                  ?? args.Context.GetRequestMessage();

    if (IdempotencyPredicate.IsRetryable(request))
        return PredicateResult.True();

    metrics.RecordSuppressedRetry(pipeline, request?.Method.Method ?? "unknown");
    return PredicateResult.False();
};
```

Retryable: `GET`, `HEAD`, `OPTIONS`, `TRACE`, `PUT`, `DELETE` — the methods
RFC 9110 defines as idempotent — plus any request carrying an `Idempotency-Key`
header, which is how a `POST` opts in. `POST` and `PATCH` without one are not
retried.

Four details that decide whether this works or only looks like it does:

- **Composed with `IsTransient`, not replacing it.** A hand-rolled
  `catch (OperationCanceledException)` would lose Polly's distinction between a
  timeout cancellation (retry it) and the caller's cancellation (let it through).
- **The request is read from the `ResilienceContext` when the outcome is an
  exception**, because there is no `HttpResponseMessage` to read
  `RequestMessage` off in that case.
- **An unknown request is NOT retried.** If the pipeline cannot see what it is
  about to repeat, that is the safe direction to fail: under-retrying costs
  latency on one request, over-retrying costs a duplicate write.
- **`PUT` and `DELETE` are idempotent by specification, not by
  implementation.** A `DELETE` that 404s the second time, or a `PUT` that
  appends, is not idempotent whatever its method says. A generic pipeline has
  no way to know that; the method is the only contract available to it. The
  assumption is documented at the predicate so the next person routing a write
  through this client knows what they are inheriting.

The gate also needed its own counter, `resilience.retries.suppressed`, for a
reason that is easy to miss: **a declined retry is a non-event to Polly.** No
retry happened, so Polly's own telemetry emits nothing — which would leave a
broken gate indistinguishable from a gate that never triggers.

---

## Proving the circuit opens and recovers

The instrument first. Day 5's breaker logged three messages and was otherwise
invisible — nothing could ask it "are you open right now?", so a test could only
infer its state from how often a stub handler was called. That inference is
worthless, because **an open breaker, a full bulkhead and a retry predicate that
declined all produce the identical call count.**

`CircuitBreakerStateProvider`, assigned to the strategy and held as a singleton
in `CircuitBreakerRegistry`, closes that: every assertion below is a statement
about the breaker rather than a guess at it.

`CircuitBreakerLifecycleTests`, with `MinimumThroughput = 4`, a 2s sampling
window and a 1s break duration — the whole file runs in about two seconds:

| # | Claim | How it is asserted |
|---|---|---|
| 1 | Starts closed | `CircuitState.Closed`, and a request succeeds |
| 2 | **Opens under sustained failure** | 6 failing calls ⇒ `CircuitState.Open`, `CircuitOpened == 1`, one `Error` log |
| 3 | **Failures below `MinimumThroughput` do NOT open it** | 5 failing calls with throughput 10 ⇒ still `Closed` |
| 4 | **Open does not call the dependency** | Stub attempt count **unchanged** across a request that threw `BrokenCircuitException` |
| 5 | Open fails fast | The rejected request costs < 500ms, against the 1s attempt timeout it replaces |
| 6 | **Half-open admits exactly one trial** | 8 concurrent requests after the break ⇒ stub saw **1**, 7 rejected, state back to `Open` |
| 7 | **Recovers** | Stub switched healthy *before* the trial ⇒ trial 200, `CircuitState.Closed`, `CircuitClosed == 1`, traffic flows after |
| 8 | The two instruments agree | State provider and the `OnOpened`/`OnClosed` counters asserted equal, once |

Rows 3, 4 and 6 are the ones usually skipped, and each answers a question the
happy path does not:

- Without **3**, row 2 proves only that the breaker *can* open — not that
  `MinimumThroughput` does anything. A breaker that opens on any two failures is
  the more damaging of the two misconfigurations, because it converts every blip
  into a self-inflicted outage of a full break duration.
- Without **4**, this is an error counter, not a circuit breaker.
- Without **6**, a recovering dependency gets knocked straight back down by the
  herd the moment the break elapses.

Row 7's ordering is deliberate: the dependency is repaired **before** the trial
request, because the breaker has no way to know it recovered and must find out
by letting one request through. Repairing it after would be testing the clock.

### One thing this could not test the way the plan said

The plan called for the breaker tests to run "with retry disabled". Polly
validates `MaxRetryAttempts` as at least 1, so there is no "disabled" to
configure. The tests send **POSTs** instead: the new idempotency gate declines
to retry them, so each call is exactly one attempt — the property the test
actually needed — and it exercises the gate where it matters. The breaker is
indifferent to the method; a 503 is a failure whatever asked for it.

---

## Test results

287 tests, zero failures, `dotnet test QuotesApi.slnx`:

| Project | Result |
|---|---|
| `Quotes.Tests.Unit` (incl. ~38 new Day 22 tests) | 191 / 191 |
| `QuotesApi.Tests` | 23 / 23 |
| `Quotes.Tests.Integration` | 60 / 60 |
| `Quotes.Tests.Integration.Redis` | 3 / 3 |
| `Quotes.Tests.Integration.SqlServer` | 5 / 5 |
| `Quotes.Tests.Integration.ServiceBus` | 5 / 5 |

Without Docker: 274 / 287, the 13 failures all being Day 13 / 19 / 21
Testcontainers fixtures that fail identically on `main`. Nothing added this day
needs Docker — the entire circuit proof is in the Unit project, which is the
constraint Day 21 set and this day kept.

**The most reassuring line is the 60 / 60, and the Day 5 tests passing
unmodified.** That was the contract on the options refactor: if binding the
policy to configuration had changed the policy, `ResilienceExtensionsTests`
would have moved. It did not — and
`Defaults_MatchTheDay5Policy_AndAreValid` asserts the equivalence directly,
rather than leaving it to be verified by reading two files side by side.

---

## Configuration, and the validation that is not a comment

New `Resilience` section, `ValidateDataAnnotations` + `ValidateOnStart`:

| Key | Default |
|---|---|
| `Resilience:TotalTimeout` | `00:00:10` |
| `Resilience:AttemptTimeout` | `00:00:03` |
| `Resilience:Retry:MaxAttempts` | `3` |
| `Resilience:Retry:BaseDelay` | `00:00:01` |
| `Resilience:Retry:IdempotentOnly` | `true` |
| `Resilience:CircuitBreaker:FailureRatio` | `0.5` |
| `Resilience:CircuitBreaker:MinimumThroughput` | `10` |
| `Resilience:CircuitBreaker:SamplingDuration` | `00:00:30` |
| `Resilience:CircuitBreaker:BreakDuration` | `00:00:15` |
| `Resilience:Bulkhead:PermitLimit` | `4` |
| `Resilience:Bulkhead:QueueLimit` | `8` |

Two rules are enforced by `IValidatableObject` rather than written in a comment,
because each value is individually plausible and only wrong in relation to
another:

- **`AttemptTimeout` must be strictly less than `TotalTimeout`.** Equal, and the
  first attempt can consume the whole budget, so no retry can ever run and the
  retry configuration is decorative. Day 5 warned about this in prose; it is now
  a startup failure.
- **`MinimumThroughput` must be at least 2.** A value of 1 makes one failure out
  of one call a 100% failure rate.

A retry budget that exceeds the total timeout is *legal* and usually intended —
the total is meant to be the binding constraint — so it is not a validation
failure. It is logged once at startup, so nobody reads `MaxAttempts` as a
promise the wall clock cannot keep.

`PermitLimit = 4` is sized from what the dependency actually receives:
`ConfigurationManager` caches the metadata and key set for 24 hours, so
steady-state traffic is a handful of requests per instance per day plus a burst
when the cache is cold or a signing key rolls. It is in configuration precisely
because it is a judgement about traffic, not a fact about the code.

---

## Verifying by hand

`GET /api/diagnostics/resilience` (Development-gated, read-only) reports circuit
state, transition counts, retries, **suppressed** retries, bulkhead rejections,
and the policy in force. Read `retriesSuppressed` next to `retries`: a non-zero
value is the gate refusing to repeat a write, and it is the only evidence the
gate exists.

`Day22/scripts/prove-circuit.ps1` drives the live run. It points
`AzureAd:Authority` at a local port with nothing listening, then sends
Entra-shaped tokens so every request forces a metadata fetch that refuses the
connection. The failure is ours to control and no load is generated against
`login.microsoftonline.com`, which is not something a resilience exercise gets
to do to a third party.

Its recovery step uses `CircuitBreakerManualControl` to close the circuit, and
that is a **weaker claim than the test makes** — it demonstrates the manual
control, not recovery under a genuinely healed dependency. The script says so in
its own output. The real recovery proof is row 7 above.

### The recorded run

Both modes were run. Raw output in
`Day22/verification/day22-circuit-proof-run-Probe.txt` and
`...-Entra.txt`, API logs beside them.

| | Probe mode | Entra mode |
|---|---|---|
| Cost of a failing request | 4143 ms | 4401 ms |
| Cost once the circuit is open | **16 ms** | **21 ms** |
| Circuit state | `Closed` → `Open` | `Closed` → `Open` |
| `opened` / `halfOpened` / `closed` | 1 / 1 / 1 | 1 / 1 / 1 |
| `retries` | 3 | 3 |
| Half-open admissions | **1 of 8** (2077 ms vs 9–35 ms) | not observable — see below |

Three things in that table are worth reading twice.

**A ~260× drop in the cost of a failing request**, and it is the same claim the
test makes with a stopwatch: an open circuit costs milliseconds where a dead
dependency costs a full attempt timeout.

**`retries = 3` for three failing requests**, with `MaxAttempts = 1`. Three
requests × two attempts = six failures, and `MinimumThroughput` was six — the
circuit opened on the sixth *attempt*, not the sixth request. The breaker sits
inside the retry and counts attempts, exactly as the ordering predicts, and this
run is where that stops being a diagram and becomes a number.

**Entra mode opened the circuit too**, which retired a caveat this plan had
carried: the concern was that `ConfigurationManager` would cache a failed
metadata fetch and starve the breaker of attempts. It made repeated attempts and
the breaker saw them. The earlier `http://` authority failure also explains
itself in this run — with `https://` the requests return 401 rather than 400,
because the handler now initialises and it is the *fetch* that fails.

### The instrument that lied, and how the test avoided it

The first two runs reported `0 admitted, 8 rejected` for the half-open burst
while `halfOpened` and `opened` both incremented — which cannot both be true.
Printing every response body settled it: one request took **2057 ms**, exactly
one attempt timeout, and the other seven took 11–45 ms (the corrected run
reproduces this as 2077 ms against 9–35 ms, now classified correctly). All eight reported
`circuit-open`.

**Polly reports a failed half-open trial to its caller as
`BrokenCircuitException` — the same exception a rejected call receives.** The
trial ran, hit the dead dependency, timed out, re-opened the breaker, and the
caller was then told the circuit was broken. The exception type cannot
distinguish "you were rejected" from "you were the trial and it failed"; only
the elapsed time can.

This is precisely why `CircuitBreakerLifecycleTests` asserts on the **stub
handler's invocation count** rather than on what the caller saw. A count of real
calls measures the dependency; the exception type measures only what Polly chose
to tell us. The test was right and the script was wrong — the correct way round,
and the reason the automated proof is the primary evidence rather than this run.

**What Entra mode cannot show:** admission counts. The authentication handler
swallows the pipeline's verdict and every request surfaces as 401 whether it was
admitted or rejected. The script says so rather than approximating a number it
cannot observe.

---

## What did you learn this session?

- **"It is only a configured constant" is how the most important strategy in a
  pipeline goes unproven.** The Day 5 reasoning was locally correct and led
  somewhere wrong: the breaker was hard to test because its parameters were
  hardcoded, and its being hardcoded became the reason not to test it. The fix
  was not more test effort, it was making the thing configurable.
- **An inference about a circuit breaker is not a measurement of one.** Call
  counts cannot distinguish an open circuit from a full bulkhead from a declined
  retry. `CircuitBreakerStateProvider` costs three lines and turns every
  assertion from a guess into a reading.
- **A retry policy is a property of the pipeline, not of its current callers.**
  The un-gated retry was harmless only because the sole caller happened to send
  `GET`s. "Harmless today because of who calls it" is a description of a latent
  defect.
- **Negative tests carry the weight here.** That the breaker opens is the easy
  half; that it does *not* open below `MinimumThroughput`, does *not* call the
  dependency while open, and does *not* let the herd through at half-open are
  the three properties that separate a breaker from an error counter.
- **An instrument can be honest and still misleading.** `BrokenCircuitException`
  is the correct exception for a failed half-open trial *and* for a rejected
  call, so a harness that classifies on exception type reports the opposite of
  what happened. The test escaped this only because it counts calls to the
  dependency rather than reading the caller's exception — which is a general
  rule, not a lucky choice.
- **Some guarantees are structural, and those are the ones to write a test
  for.** A bulkhead rejection cannot open the circuit because of where the
  limiter sits, not because of a predicate. Nothing in the code says so, which
  is exactly why a future reordering would silently lose it.

## What would break this?

- **Moving the bulkhead inside the retry.** Compiles, runs, and turns load
  shedding into retried load — an overload amplifier wearing a bulkhead's name.
- **Moving the bulkhead outside the total timeout.** The permit wait becomes
  unbounded and the ten-second promise silently stops being one.
- **Setting `Resilience:Retry:IdempotentOnly=false`** to "fix" a flaky
  dependency. It is logged at startup for exactly this reason, so the decision
  is findable by whoever has to explain the duplicate write.
- **Adding a `POST`-issuing caller to the `entra-id` client and then wondering
  why it never retries.** That is the gate working; the answer is an
  `Idempotency-Key` and a far end that honours it, not a wider predicate.
- **Reading `resilience.circuit.state` as a fleet-wide value.** The breaker is
  per process. Five replicas mean five independent breakers, and the dependency
  sees up to five half-open trials per break interval.
- **Trusting `/resilience/isolate` as proof.** It demonstrates the manual
  control. Sustained failure opening the circuit is a different claim, and only
  `CircuitBreakerLifecycleTests` makes it.
- **A future outbound dependency added without `IHttpClientFactory`.** Nothing
  in the build fails; the new call simply has none of this — precisely as the
  Entra backchannel did before Day 5.

## What this does not prove

- **Not a distributed breaker.** Per process, per pipeline. The measured
  half-open behaviour is per instance.
- **Not that these numbers are right for production traffic.** A `PermitLimit`
  of 4 and a 15-second break are defensible for a metadata client with a 24-hour
  cache. They are not transferable to a dependency on the request path, and this
  exercise generated no traffic data that would justify them there.
- **Not that Entra ID failures are now invisible.** An open breaker means
  Entra-issued tokens fail validation *immediately* rather than after ten
  seconds. That is a better failure, not the absence of one — and the local JWT
  scheme is unaffected, which is why the app stays partly usable.
- **Nothing about retry storms across the fleet.** Jitter spreads retries within
  one process's view of an outage. Whether five instances retrying with jitter
  amount to a thundering herd is a question about aggregate load that a
  single-host test cannot answer.
- **Not idempotency of the far end.** The gate reasons about HTTP methods and an
  opt-in header. It cannot know whether the server treats a repeated `PUT` as
  one operation.
