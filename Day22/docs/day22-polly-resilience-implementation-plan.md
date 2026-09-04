# Day 22 — Resilience with Polly

## Detailed task prompt

> Wrap an outbound dependency with Polly: retry-with-backoff (idempotent only),
> a circuit breaker, a timeout, and a bulkhead. Then prove the circuit opens
> under sustained failure and recovers.

## What changed once it was built

The plan above is kept as written. Nine things came out differently, and two of
them are limits on the evidence rather than changes to the design.

**1. `MaxRetryAttempts = 0` is not a legal value, so the breaker tests
neutralise the retry differently.** The plan said the lifecycle test would run
"with retry disabled for this pipeline instance so attempt counting stays
honest". Polly validates `MaxRetryAttempts` as at least 1, so there is no
"disabled" to configure. The tests send **POSTs** instead: the Day 22
idempotency gate declines to retry them, so each call is exactly one attempt,
which is the property the test actually needed. It also exercises the gate in
the place where it matters. The circuit breaker is indifferent to the method — a
503 is a failure whatever asked for it — so nothing about the breaker's
behaviour is changed by the substitution.

**2. "A bulkhead rejection must not count as a dependency failure" turned out to
be structural, not a predicate.** The plan implied a predicate would exclude
`RateLimiterRejectedException` from the breaker. It does not need one: the
limiter sits outside both the retry and the breaker, so neither can ever see a
rejection the limiter raised above them. That is a better guarantee than a
predicate — there is nothing for a future edit to forget — but it is invisible
in the code, which is exactly why `BulkheadTests` asserts it anyway.

**3. The queue-wait histogram was dropped.** `resilience.bulkhead.queue.wait`
was in the plan's instrument table and is not in `ResilienceMetrics`. Polly's
rate-limiter strategy does not surface the permit wait, and measuring it would
mean wrapping the limiter in a timing strategy of our own — at which point the
number is our wrapper's latency, not the limiter's queue. An instrument that
reports something adjacent to what its name claims is worse than no instrument,
so `resilience.bulkhead.rejections` carries the whole story: shed or not shed.

**4. `AddResilientHttpClients` has two overloads, and that is how the
"Day 5 tests must pass unmodified" contract is actually enforced.** The Day 5
unit tests build a bare `ServiceCollection` with no `IConfiguration` at all, so
a signature that required one would have forced them to be edited — and editing
them would have destroyed the only check that the options refactor preserved the
policy. The no-argument overload binds nothing and therefore runs on
`ResilienceOptions`' defaults, which are the Day 5 constants.
`ResilienceOptionsValidationTests.Defaults_MatchTheDay5Policy_AndAreValid`
asserts that equivalence directly, so the claim does not rest on reading two
files side by side.

**5. The idempotency gate defaults to NOT retrying when it cannot see the
request.** An exception outcome carries no `HttpResponseMessage`, so the request
has to come from the `ResilienceContext`. If it is unavailable there too, the
pipeline does not know what it is about to repeat, and `IdempotencyPredicate`
returns false. That direction is the decision: under-retrying costs latency on
one request, over-retrying costs a duplicate write.

**6. Recovery in the live script is demonstrated with the manual control, which
is a weaker claim, and the script says so.** Repairing a dead authority
mid-run — standing up a metadata document the JwtBearer handler will accept —
is more machinery than a walkthrough script should carry. So `prove-circuit.ps1`
closes the circuit through `CircuitBreakerManualControl` and prints, in the run
output, that this demonstrates the manual control rather than recovery under a
genuinely healed dependency. The real recovery proof is
`Circuit_WhenDependencyRecovers_ClosesAgain`, where the stub is switched to
healthy *before* the trial request, so the breaker has to find out by letting
one through.

**7. Two negative tests were added that the plan did not list, and they carry
more weight than some of the positive ones.**
`Circuit_WithFailuresBelowMinimumThroughput_StaysClosed` — without it, the
opening test proves only that the breaker *can* open, not that the throughput
guard does anything, and a breaker that opens on any two failures is the more
damaging of the two misconfigurations. And
`Permits_AreReleased_EvenWhenTheRequestFails` — a limiter that leaks permits
degrades to `PermitLimit=0`, whose symptom is total, permanent failure of the
dependency with nothing in the logs; releasing only on the success path is the
shape that bug takes.

**8. The planned integration test was not written.** `ResilienceDiagnosticsTests`
would need a `QuotesApiFactory` variant that forces `Diagnostics:Enabled`,
because the diagnostics group is Development-gated and the test host's
environment is not something to assume. It would prove that the endpoint is
mapped — worth having, and it is the one item from the plan's test list that is
outstanding. Everything the proof actually rests on is in the unit suite, which
is where the plan said the primary evidence would live.

**9. It compiled and the suite is green on the first run.** 287 tests, zero
failures, with Docker running for the three container-backed projects:

| Project | Result |
|---|---|
| `Quotes.Tests.Unit` (includes all ~38 new Day 22 tests) | 191 / 191 |
| `QuotesApi.Tests` | 23 / 23 |
| `Quotes.Tests.Integration` | 60 / 60 |
| `Quotes.Tests.Integration.Redis` | 3 / 3 |
| `Quotes.Tests.Integration.SqlServer` | 5 / 5 |
| `Quotes.Tests.Integration.ServiceBus` | 5 / 5 |

Without Docker the run is 274 / 287, and the 13 failures are all
`ArgumentException: Docker is either not running or misconfigured` raised in
`RedisFixture`, `ServiceBusEmulatorFixture` and `MsSqlContainerFixture`
constructors — Day 13 / 19 / 21 fixtures that fail identically on `main`.
Acceptance criterion 5 holds: nothing added here needs Docker, and the whole
circuit-breaker proof runs in the Unit project.

**The number that matters most is the 60 / 60 and the Day 5 tests passing
unmodified.** That was the stated contract on the options refactor: if binding
the policy to configuration had changed the policy, `ResilienceExtensionsTests`
would have moved. It did not, so the defaults do reproduce Day 5's inline
constants — and `Defaults_MatchTheDay5Policy_AndAreValid` asserts that directly
rather than leaving it to be checked by reading two files side by side.

Both risks this plan flagged as unverifiable from the sandbox resolved in
favour of the design as written, and neither fallback was needed:

| Flagged risk | Outcome |
|---|---|
| `ResilienceContext.GetRequestMessage()` might not exist on the pinned version, forcing a `no-retry` second pipeline | It exists on `Microsoft.Extensions.Http.Resilience` 10.9.0. The gate reads the request from the context on exception outcomes as designed. |
| `AddRateLimiter` / `HttpRateLimiterStrategyOptions` might need `Polly.RateLimiting` added explicitly | It arrives transitively with `Microsoft.Extensions.Http.Resilience`. No package was added. |
| Polly's 500ms floors might reject the test durations | The test values (2s sampling, 1s break) sit above the floor and the lifecycle test runs in roughly two seconds. |

The **live run** has since been executed in both modes and is recorded in
`Day22/verification/`. It cost three attempts to get the harness right, and each
failure was a fact about the caller rather than about the pipeline: an `http://`
authority that JwtBearer refuses to initialise with, then a classifier that read
`BrokenCircuitException` as "rejected" when Polly also raises it for a failed
half-open trial. The numbers, and what that second mistake teaches about
choosing an instrument, are in the submission.

## Branch base

Branched off `main` at `a07564d`, which contains Day 21 (PR #49 merged), so
`Day7/piece2` is the current code and this branch starts from it.

## Where this starts, which is not from zero

Day 5, Task 7 already put a Polly v8 pipeline on this API's one outbound HTTP
call. `QuotesApi/Extensions/ResilienceExtensions.cs` registers a named client
`entra-id`, assigns it to `JwtBearerOptions.Backchannel` in
`InfrastructureExtensions`, and wraps it in: total timeout (10s) → retry (3,
exponential, jittered) → circuit breaker (0.5 ratio / 30s window / 10 minimum
throughput / 15s break) → attempt timeout (3s). `Quotes.Tests.Unit/ResilienceExtensionsTests.cs`
covers three properties with a stubbed primary handler.

So the honest reading of Day 22 is that three of the five asked-for pieces exist
and two do not, and that the two missing ones are the two that matter:

| Asked for | State today | Day 22 |
|---|---|---|
| Timeout | Two of them, correctly nested | Keep; move the numbers into options |
| Retry with backoff | Present, jittered, logged | **Not gated on idempotency** — see below |
| Circuit breaker | Configured | Configured is not proven; **no test opens it** |
| Bulkhead | Absent | Add, as a concurrency limiter |
| Proof of open → recover | Absent | The deliverable |

Two of those deserve to be named as defects rather than as gaps.

**1. The retry is not idempotency-gated.** `HttpRetryStrategyOptions.ShouldHandle`
defaults to handling 5xx, 408, `HttpRequestException` and inner-timeout
cancellations — and it does that regardless of HTTP method. On the `entra-id`
client this is harmless by accident: every request the JwtBearer handler issues
is a `GET`. That is a property of today's only caller, not of the pipeline. The
pipeline is a reusable registration; the first `POST` routed through it inherits
a retry policy that will re-send a write after a 503, and a 503 does not tell
you whether the server processed the request before it fell over. The task's
parenthetical — *idempotent only* — is asking for the gate to be in the policy,
not in the caller's good manners.

**2. The circuit breaker's parameters are constants, which is exactly why it was
never proven.** The Day 5 test file says so out loud: opening the breaker "needs
at least MinimumThroughput (10) failing calls inside a 30 second window and
would trade seconds of test runtime for a property that is a configured constant
rather than logic." That reasoning was sound given hardcoded values and stops
being sound the moment the values are bound to configuration. Binding them is
what turns "prove the circuit opens and recovers" from a ten-second sleep into a
sub-second deterministic test. The proof requirement and the options refactor are
the same piece of work.

## Which dependency, and what is deliberately left alone

This API's outbound dependencies, all of them:

| Dependency | Transport | Wrapped in Polly? |
|---|---|---|
| Entra ID OIDC metadata + JWKS (`AzureAd:Authority`) | HTTPS via `IHttpClientFactory` | **Yes — the subject of this day.** The only outbound HTTP this app issues, and a failure here fails every Entra-issued token in flight. |
| SQLite / SQL Server | ADO.NET, in-process provider | No. `EnableRetryOnFailure` is EF's own execution strategy and already the right tool; a Polly retry above it would multiply attempts, and a retry around an open transaction is a correctness bug, not a resilience feature. |
| Azure Service Bus (`ServiceBusQuoteEventPublisher`) | AMQP, Azure SDK | No. `ServiceBusClientOptions.RetryOptions` already retries with backoff inside the SDK. Stacking Polly on top gives 3 × 3 attempts and a breaker that cannot see the SDK's internal ones — retry amplification, the failure mode that turns a brownout into an outage. |
| Redis L2 (`Cache:Redis`) | RESP, StackExchange.Redis | No. Day 21 already chose the correct behaviour — `AbortOnConnectFail=false` and degrade to L1 — and `HybridCache` swallows an L2 failure by design. A breaker in front of it would add a second, redundant open/closed state. |
| Application Insights | HTTPS, Azure Monitor exporter | No. Not routed through `IHttpClientFactory`; the exporter owns its own transmission and retry. |

Writing that table out is part of the exercise. "Wrap an outbound dependency
with Polly" is easy to over-apply, and the most common production mistake with
Polly is not a missing policy — it is two retry policies stacked on the same
call, each unaware of the other.

There is a second, sharper reason to keep the scope to `entra-id`: it is a
**read-only** dependency, which means the idempotency gate cannot be
demonstrated on it. Rather than invent a fake write dependency to make the
demonstration possible, the gate is implemented in the shared pipeline builder
and proven by a unit test that pushes a `POST` through that same pipeline. The
policy is the artefact; the caller list is incidental.

## The pipeline, with the bulkhead placed

Polly v8 has no strategy called "bulkhead". The v7 `BulkheadPolicy` was replaced
by the rate-limiter strategy over `System.Threading.RateLimiting`, and a bulkhead
is that strategy configured with a `ConcurrencyLimiter`. Same semantics — a cap
on simultaneous executions plus a bounded queue — different name. Anyone
grepping the code for "bulkhead" should find it, so the registration is named
`"bulkhead"` and the reason is in a comment.

Strategies added first sit outermost. The order becomes:

```
total timeout  ->  bulkhead  ->  retry  ->  circuit breaker  ->  attempt timeout  ->  request
```

The only genuinely arguable position is the bulkhead's, so:

**Outside the retry, not inside.** A bulkhead exists to bound how much of this
process is tied up waiting on one dependency. One logical operation should hold
one permit for its whole life, retries and backoff delays included — that is the
resource actually being consumed. Placed *inside* the retry, each attempt would
acquire and release separately, and worse: a `RateLimiterRejectedException` from
a full bulkhead would land inside the retry's `ShouldHandle` and be treated as a
transient failure. Retrying a load-shed rejection is the definition of making an
overload worse. The rejection must fast-fail outward, which requires the limiter
to sit outside the thing that retries.

**Inside the total timeout, not outside.** Waiting for a permit is waiting. With
the limiter outermost, a caller could queue for a permit with nothing bounding
that wait, and the ten-second promise made to an inbound request would be a
promise about the request only after it got lucky. `QueueLimit` bounds the number
of waiters; the total timeout bounds how long any one of them waits.

Sizing, stated rather than guessed: the `entra-id` client's real traffic is
metadata and key-set fetches that `ConfigurationManager` refreshes on an
interval and caches for 24 hours — a handful of requests per instance per day in
the steady state, and a small burst when the cache is cold or a key rolls. A
`PermitLimit` of 4 with a `QueueLimit` of 8 is therefore generous for normal
operation and tight enough to actually shed under the pathological case, which
is the one worth defending: metadata refresh failing while inbound token
validations pile up behind it. The numbers go in configuration precisely because
they are a judgement about traffic, not a fact about the code.

## Retry only if idempotent

The gate, in the pipeline builder rather than at the call sites:

```csharp
ShouldHandle = static args =>
    IsIdempotent(args.Outcome.Result?.RequestMessage ?? args.Context.GetRequestMessage())
        ? HttpClientResiliencePredicates.IsTransient(args.Outcome)
        : PredicateResult.False()
```

`IsIdempotent` returns true for `GET`, `HEAD`, `OPTIONS`, `TRACE`, `PUT` and
`DELETE` — the methods RFC 9110 defines as idempotent — and for any request
carrying an `Idempotency-Key` header, which is how a `POST` opts in. `POST` and
`PATCH` without that header are not retried.

Three details that decide whether this works or only looks like it does:

- **`PUT`/`DELETE` are idempotent by specification, not by implementation.** A
  `DELETE` that returns 404 on the second call, or a `PUT` that increments a
  counter, is not idempotent whatever the method says. The predicate trusts the
  method because that is the only contract available to a generic pipeline; the
  comment says so, so the next person routing a write through this client knows
  which assumption they are inheriting.
- **The request message has to be reachable from the predicate.** On a failed
  outcome, `args.Outcome.Result` may be null (an exception, not a response), and
  then `RequestMessage` is not available from it. `ResilienceContext` carries the
  request via `HttpResilienceContextExtensions` (`GetRequestMessage`); which
  accessor is available on the pinned `Microsoft.Extensions.Http.Resilience`
  10.9.0 must be **verified at implementation time**. If neither is reachable,
  the fallback is two registered pipelines — `entra-id` retrying and a
  `no-retry` variant — rather than a predicate that silently defaults to
  retrying everything.
- **Timeout cancellations must stay retryable, and caller cancellation must
  not be.** `IsTransient` handles the inner timeout's `OperationCanceledException`
  but a genuine caller abort must fall straight through. Polly v8 distinguishes
  them; a hand-rolled `catch (OperationCanceledException)` would not, which is
  why the predicate composes with `IsTransient` instead of replacing it.

## Making the breaker observable, which is what makes it provable

The breaker's state today is invisible: it exists inside the pipeline, logs three
messages, and nothing can be asked "are you open right now?". Two Polly v8
facilities close that:

- **`CircuitBreakerStateProvider`** — assigned to
  `HttpCircuitBreakerStrategyOptions.StateProvider`, exposes
  `CircuitState` (`Closed` / `Open` / `HalfOpen` / `Isolated`). Registered as a
  singleton so a diagnostics endpoint and the tests can both read it. This is
  the instrument the proof is built on: asserting `CircuitState.Open` is a
  statement about the breaker, whereas asserting "the handler was called fewer
  times than I asked" is an inference about it.
- **`CircuitBreakerManualControl`** — assigned to `ManualControl`, allows
  `IsolateAsync` / `CloseAsync`. Used for a Development-only diagnostics route
  that trips the breaker on demand, so the live demonstration does not depend on
  making `login.microsoftonline.com` fail. It is **not** used for the automated
  proof: isolating the breaker by hand proves the manual control works, not that
  sustained failure opens it.

Alongside, a `ResilienceMetrics` class in the shape of Day 20's `OutboxMetrics`
and Day 21's `CacheMetrics`, meter registered in `ObservabilityExtensions`:

| Instrument | Type | Meaning |
|---|---|---|
| `resilience.retries` | counter, tagged `pipeline`, `outcome` | Retries attempted |
| `resilience.retries.suppressed` | counter, tagged `method` | Non-idempotent requests **not** retried — the gate, made visible |
| `resilience.circuit.state` | observable gauge | 0 closed / 1 half-open / 2 open, read from the state provider |
| `resilience.circuit.transitions` | counter, tagged `to` | Open / half-open / closed transitions |
| `resilience.bulkhead.rejections` | counter | Requests shed by the limiter |
| `resilience.bulkhead.queue.wait` | histogram | Time spent waiting for a permit |

Polly v8 also emits its own meter (`Polly`, counter
`resilience.polly.strategy.events` tagged with pipeline / strategy / event
names). If it is present on the pinned version it becomes the source of truth
and the counters above become the cross-check — the same rule Day 21 applied to
`HybridCache`'s own meter. Verify; do not assume either way.

## Proving open → recover

Two forms, for the two different things "prove" can mean.

### The deterministic test — the primary evidence

In `Quotes.Tests.Unit/ResilienceTests`, with a stubbed primary handler and the
breaker's parameters bound to test values (`MinimumThroughput` 4,
`SamplingDuration` 500ms, `BreakDuration` 300ms, retry disabled for this pipeline
instance so attempt counting stays honest):

1. **Closed.** State provider reports `Closed`; a request succeeds.
2. **Opens under sustained failure.** Drive N failing requests where N ≥
   `MinimumThroughput` inside the sampling window. Assert: state is `Open`; the
   `OnOpened` log fired once; `resilience.circuit.transitions{to=open}` is 1.
3. **Open fails fast, without calling the dependency.** Record the stub's
   attempt count, issue another request, assert `BrokenCircuitException` and
   that the attempt count **did not move**. This is the assertion that matters
   most and the one most often skipped: an open breaker that still calls the
   dependency is not a breaker. It is also worth asserting the failure is fast
   (well under the 3s attempt timeout) — the entire benefit is that a dead
   dependency costs microseconds rather than held threads.
4. **Half-open lets exactly one trial through.** Wait past `BreakDuration`, then
   issue several concurrent requests against a still-failing stub. Assert the
   stub saw **one** attempt and the rest were rejected, and that the breaker
   returned to `Open` — a half-open breaker that admits the whole herd is how a
   recovering dependency gets knocked back down.
5. **Recovers.** Wait past `BreakDuration` again with the stub now healthy.
   Assert: the trial request succeeds, state returns to `Closed`, `OnClosed`
   logged, `transitions{to=closed}` is 1, and subsequent requests flow normally.

Retry is exercised in its own tests, separately, because a retry loop inside the
breaker test makes the attempt counts arithmetic instead of evidence. The
existing Day 5 tests stay as they are; these are added beside them.

Timing: with a 300ms break duration the whole sequence runs in roughly a second
of real time. `Task.Delay` on the break duration is unavoidable — Polly v8's
breaker reads the clock through `TimeProvider`, so **if** the pinned version
allows a `TimeProvider` to be supplied to the strategy options, the waits become
virtual and the test becomes instant and non-flaky. Check at implementation
time; if it cannot be injected, the real delays stay and the durations are kept
small enough that CI does not notice.

### The live run — the evidence for the write-up

A test proves the mechanism; a run shows the shape. `Day22/scripts/prove-circuit.ps1`
drives it against a locally running instance:

1. Point `AzureAd:Authority` at a **fault-injecting stub** (a ~30-line minimal
   API in `Day22/tools/flaky-authority`, or `Cache`-style config pointing at a
   local port that nothing is listening on) so the failure is ours to control
   and no traffic reaches Microsoft.
2. Sustained load of authenticated requests carrying an Entra-shaped token, so
   every request forces a metadata fetch attempt.
3. Poll `GET /api/diagnostics/resilience` — the new route reporting circuit
   state, transition counts, retry counts, suppressed retries and bulkhead
   rejections — and record the transition from `Closed` to `Open`, the drop in
   response latency once open (fast-fail replacing timeout), the single
   half-open trial, and the return to `Closed` after the stub is made healthy.
4. Capture: the raw script output to `Day22/verification/day22-circuit-proof-run.txt`,
   and screenshots of the state timeline and the Jaeger view — where an open
   breaker shows as a request span with **no** outbound HTTP child span, the same
   visual proof Day 21 used for a cache hit.

The diagnostics route is read-only and lives in the existing
`/api/diagnostics` group, which `MapDiagnosticsEndpoints` refuses to map at all
unless the app is in Development or the escape hatch is set explicitly — so the
new routes inherit that gate rather than re-implementing it. The manual-control trip route is
Development-only too, and exists for demonstration convenience, not for the
proof.

## Configuration

New `Resilience` section, `ResilienceOptions` with `ValidateDataAnnotations` +
`ValidateOnStart`, mirroring `CacheOptions` and `OutboxOptions`:

| Key | Default | Meaning |
|---|---|---|
| `Resilience:TotalTimeout` | `00:00:10` | The promise to the caller; covers every attempt and backoff |
| `Resilience:AttemptTimeout` | `00:00:03` | Per-attempt cap; must be well under the total or retries never run |
| `Resilience:Retry:MaxAttempts` | `3` | Retries after the first attempt |
| `Resilience:Retry:BaseDelay` | `00:00:01` | Exponential base, jittered |
| `Resilience:Retry:IdempotentOnly` | `true` | The gate. `false` is a deliberate, logged choice, not a default |
| `Resilience:CircuitBreaker:FailureRatio` | `0.5` | Fraction of failures in the window that opens it |
| `Resilience:CircuitBreaker:MinimumThroughput` | `10` | Below this, a ratio is noise |
| `Resilience:CircuitBreaker:SamplingDuration` | `00:00:30` | The window |
| `Resilience:CircuitBreaker:BreakDuration` | `00:00:15` | How long it stays open before a trial |
| `Resilience:Bulkhead:PermitLimit` | `4` | Concurrent in-flight requests to this dependency |
| `Resilience:Bulkhead:QueueLimit` | `8` | Waiters admitted before shedding |

Validation that has to be more than data annotations, because two of these are
only wrong in relation to each other:

- `AttemptTimeout` must be strictly less than `TotalTimeout`. Equal means the
  first attempt can consume the whole budget and the retry configuration is
  decorative — the exact defect the Day 5 comments warn about, now enforceable
  rather than commented.
- `AttemptTimeout × (MaxAttempts + 1)` exceeding `TotalTimeout` is legal and
  usually intended (the total is meant to be the binding constraint), but it is
  worth logging once at startup so nobody reads the retry count as a promise.
- `MinimumThroughput` must be ≥ 2; `FailureRatio` in (0, 1]. A `MinimumThroughput`
  of 1 makes the breaker open on a single blip, which is worse than no breaker
  because it converts one failure into `BreakDuration` of guaranteed failures.

`Resilience:CircuitBreaker__*` and `Resilience__Retry__*` get cleared in each
test project's `TestEnvironment` `[ModuleInitializer]`, for the reason Day 20
learned the hard way and Day 21 wrote down: an ambient environment variable that
changes a policy underneath tests that assert the default produces failures whose
cause is invisible.

## Tools required

### NuGet

| Package | Project | Purpose |
|---|---|---|
| `Microsoft.Extensions.Http.Resilience` 10.9.0 | `QuotesApi` | Already referenced. Supplies `AddResilienceHandler`, the `Http*StrategyOptions` types, and `HttpRateLimiterStrategyOptions` |
| `Polly.RateLimiting` | `QuotesApi` | The bulkhead. **Verify whether it arrives transitively** with the package above; add it explicitly only if `AddRateLimiter`/`AddConcurrencyLimiter` does not resolve |
| `Microsoft.Extensions.TimeProvider.Testing` | `Quotes.Tests.Unit` | `FakeTimeProvider`, *if* the breaker accepts an injected `TimeProvider` on this version. Not added unless it is used |

No version is asserted for the new entries — nuget.org is not reachable from
where this plan was written. Pin whatever `dotnet add package` resolves for
`net10.0`, on the same 10.0.x / 8.x lines as the neighbours already in the file.

### Already present, reused

.NET 10 SDK · xUnit + FluentAssertions · the `RecordingLoggerProvider` and
`SequencedHandler` test doubles already in `ResilienceExtensionsTests` (both get
promoted to shared doubles rather than copy-pasted) · OpenTelemetry metrics
wiring in `ObservabilityExtensions` · Jaeger, for the missing-child-span proof ·
`DiagnosticsEndpointExtensions`' Development-only route pattern

### Not required

Docker, for anything in the automated suite. Every assertion in the primary
proof runs in-process against a stubbed handler. That is a deliberate constraint
carried forward from Day 21: nothing new may require Docker to pass CI.

## Planned file changes

**New — `Day7/piece2/QuotesApi`**

```
Resilience/ResilienceOptions.cs              the four nested option groups + cross-field validation
Resilience/ResiliencePipelineNames.cs        pipeline and strategy name constants (tests assert on them)
Resilience/IdempotencyPredicate.cs           which requests may be retried, and why
Resilience/ResilienceMetrics.cs              retries / suppressed / circuit state / bulkhead rejections
Resilience/CircuitBreakerRegistry.cs         holds the StateProvider + ManualControl singletons
```

**Modified**

```
Extensions/ResilienceExtensions.cs           options-driven; adds the bulkhead, the gate, the state provider
Extensions/ObservabilityExtensions.cs        register the resilience meter
Extensions/DiagnosticsEndpointExtensions.cs  GET /api/diagnostics/resilience (Dev only); POST .../trip (Dev only)
Program.cs                                   no change: MapDiagnosticsEndpoints is already mapped (line 218)
appsettings.json                             the Resilience section
appsettings.Development.json                 shorter durations for hand-verification, if that file exists
```

**Tests**

```
Quotes.Tests.Unit/Resilience/IdempotencyPredicateTests.cs
Quotes.Tests.Unit/Resilience/RetryGateTests.cs               POST not retried, GET retried, Idempotency-Key opts in
Quotes.Tests.Unit/Resilience/CircuitBreakerLifecycleTests.cs the five-step proof
Quotes.Tests.Unit/Resilience/BulkheadTests.cs                shedding, and rejection is not retried
Quotes.Tests.Unit/Resilience/ResilienceOptionsValidationTests.cs
Quotes.Tests.Unit/ResilienceExtensionsTests.cs               kept; updated for the options-driven registration
Quotes.Tests.Integration/ResilienceDiagnosticsTests.cs       the endpoint reports the real state
```

**Docs and evidence — `Day22/`**

```
docs/day22-polly-resilience-implementation-plan.md   this file
docs/day22-polly-resilience-prompt.md                the prompt, per the Day 19-21 convention
docs/day22-polly-resilience-submission.md            written against the measured run
scripts/prove-circuit.ps1
verification/day22-circuit-proof-run.txt
verification/screenshots/
```

## Implementation sequence

1. `ResilienceOptions` + cross-field validation + tests. Nothing else can be
   proven while the parameters are constants, so this is first — not as
   housekeeping, but because it is the enabling change.
2. Rewrite `ResilienceExtensions` to read options, keeping the existing strategy
   order and behaviour byte-for-byte at the default values. The Day 5 tests must
   stay green **without modification** at this step; if they need editing, the
   refactor changed behaviour it was not supposed to change.
3. `CircuitBreakerRegistry` + `StateProvider` wiring. Still no behaviour change —
   only observability.
4. The circuit-breaker lifecycle test. The proof, written against a breaker whose
   behaviour has not yet been touched, so it is testing the Day 5 policy rather
   than a policy written to satisfy it.
5. `IdempotencyPredicate` + the retry gate + its tests.
6. The bulkhead, outside the retry, with the rejection-is-not-retried test.
7. `ResilienceMetrics` + the diagnostics endpoint.
8. The live run, the raw output, the screenshots.
9. The submission write-up, against the recorded numbers rather than against
   this plan.

Steps 4 and 5 in that order matters. Writing the proof before changing the
policy means the test cannot be quietly shaped around whatever the new code
happens to do.

## Test strategy

### Unit — the gate

- `GET`, `HEAD`, `PUT`, `DELETE`, `OPTIONS`, `TRACE` on a 503: retried.
- `POST` and `PATCH` on a 503: **one** attempt, and
  `resilience.retries.suppressed{method=POST}` incremented.
- `POST` with an `Idempotency-Key` header on a 503: retried.
- Any method on a 404: not retried (the Day 5 property, preserved).
- Caller cancellation mid-flight: surfaces as cancellation, not as a retry.

### Unit — the circuit

The five-step lifecycle above, plus:

- Failures spread thinly enough to stay under `MinimumThroughput` in the window
  do **not** open it. Without this, step 2 proves only that the breaker can open,
  not that the throughput guard works — and a breaker that opens on any two
  failures is the more damaging misconfiguration of the two.
- The state provider and the logs agree. Two instruments that could disagree
  should be asserted to agree, once.

### Unit — the bulkhead

- With `PermitLimit=1`, `QueueLimit=0` and a blocking stub, the second concurrent
  request is rejected with `RateLimiterRejectedException` rather than queued.
- That rejection is **not** retried and does not count toward the breaker's
  failure ratio. Load shedding is the system working; counting it as dependency
  failure would open the breaker because of our own back-pressure, and then keep
  it open.
- A permit is released when the operation completes, including when it completes
  by throwing. A limiter that leaks permits degrades to `PermitLimit=0` and the
  symptom is total, permanent failure of the dependency with nothing in the logs.

### Integration

- `GET /api/diagnostics/resilience` returns `Closed` on a freshly started host
  and is absent in Production.
- The whole existing suite stays green with default configuration.

## Evidence the submission will carry

| Claim | Evidence |
|---|---|
| The circuit opens under sustained failure | State provider reads `Open` after N failures; transition counter 1; `OnOpened` logged once |
| An open circuit does not call the dependency | Stub attempt count unchanged across a request that threw `BrokenCircuitException` |
| An open circuit fails fast | Elapsed time per rejected request, against the 3s attempt timeout it replaces |
| Half-open admits exactly one trial | Stub attempt count = 1 across k concurrent requests after `BreakDuration` |
| The circuit recovers | State returns to `Closed`; `transitions{to=closed}` = 1; subsequent requests succeed |
| Non-idempotent requests are not retried | Attempt count 1 for `POST`; `retries.suppressed` incremented |
| The bulkhead sheds rather than queues unboundedly | Rejection count under a load exceeding permits + queue |
| It all holds in the running app, not only in a test | The live run output and the Jaeger span with no outbound child |

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| The options refactor silently changes the Day 5 policy | Day 5's tests must pass unmodified at step 2; defaults chosen to reproduce the current constants exactly |
| A breaker test that sleeps, and therefore flakes in CI | Small break durations; `FakeTimeProvider` if the pinned version allows injection; no test asserting on wall-clock beyond an order of magnitude |
| Bulkhead rejections counted as dependency failures, opening the breaker under our own back-pressure | Limiter outside retry and outside breaker; asserted by test |
| `RateLimiterRejectedException` retried as transient | The predicate excludes it explicitly; asserted by test |
| The idempotency predicate cannot reach the request message on this package version | Verified at implementation time; fallback is two named pipelines rather than a predicate that defaults to permissive |
| `PUT`/`DELETE` assumed idempotent when the far end is not | Documented as the inherited assumption at the point of the predicate; `Idempotency-Key` is the explicit opt-in for anything else |
| A total timeout that makes the retry configuration decorative | Cross-field validation on `AttemptTimeout` vs `TotalTimeout`; startup log when the arithmetic is tight |
| Proving the breaker by isolating it manually and calling that a proof | The manual control is Development-only and explicitly excluded from the automated proof |
| The live run hitting Microsoft's real endpoint under sustained failure | The authority is pointed at a local fault-injecting stub; no failure load is generated against a third party |
| Polly stacked on the Service Bus / Redis / EF paths, multiplying retries | The dependency table above states which layer owns retry for each, and why nothing is added there |

## Acceptance criteria

1. `entra-id` is wrapped in all four: total + attempt timeouts, jittered
   exponential retry, circuit breaker, and a concurrency-limiter bulkhead, in
   the documented order.
2. A retry happens for idempotent requests and demonstrably does not happen for
   a `POST` without an `Idempotency-Key`.
3. The circuit breaker is proven to open under sustained failure, to fail fast
   without touching the dependency while open, to admit exactly one half-open
   trial, and to close again — each as a separate assertion on the state
   provider, not inferred from attempt counts alone.
4. Failures below `MinimumThroughput` are proven not to open it.
5. The bulkhead sheds excess load, and the rejection is neither retried nor
   counted as a dependency failure.
6. Every policy parameter is configuration-bound and validated, including the
   cross-field relationship between the two timeouts.
7. Circuit state, retry counts, suppressed retries and bulkhead rejections are
   readable from a diagnostics endpoint and emitted as metrics.
8. The full suite is green, and nothing new requires Docker for CI.
9. The submission reports the open → recover sequence from one recorded run,
   with the raw output attached.

## What this will not prove

- **Nothing about a distributed breaker.** The breaker is per process, per
  pipeline. Five container-app replicas mean five independent breakers, and the
  dependency sees up to five trial requests per break interval, not one. The
  measured half-open behaviour is per instance and the write-up will say so.
- **Nothing about whether these numbers are right for production traffic.** A
  `PermitLimit` of 4 and a 15-second break are defensible for a metadata client
  with a 24-hour cache. They are not transferable to a dependency on the request
  path, and the exercise generates no traffic data that would justify them there.
- **Not a claim that Entra ID failures are now invisible.** An open breaker
  means Entra-issued tokens fail validation *immediately* rather than after ten
  seconds. That is a better failure, not the absence of one. The local JWT
  scheme is unaffected, which is worth stating because it is the reason the app
  remains partly usable.
- **Nothing about retry storms across the fleet.** Jitter spreads retries within
  one process's view of an outage; whether five instances retrying with jitter
  amount to a thundering herd is a question about aggregate load that a
  single-host test cannot answer.
- **Not idempotency of the far end.** The gate reasons about HTTP methods and an
  opt-in header. It cannot know whether the server actually treats a repeated
  `PUT` as one operation.
