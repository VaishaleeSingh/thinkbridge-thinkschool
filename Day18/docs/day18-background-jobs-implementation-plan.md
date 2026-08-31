# Day 18 — Background jobs implementation plan

## Detailed task prompt

Imagine that one of our API requests needs to do work that takes several
seconds, such as generating a report. We do not want the user to keep waiting
while that work runs on the request thread. Build a small background-job flow
that accepts the request, places the work in a queue, and immediately tells the
user that the job has been accepted.

Create a `BackgroundService` that keeps listening to the queue and processes one
job at a time. Make the flow easy to understand and observe: return `202
Accepted` when a job is queued, provide an endpoint where the user can check its
status, and record whether it is queued, running, completed, failed, or
cancelled. Keep request-only objects such as `HttpContext`, bearer tokens, and a
scoped `DbContext` out of the queue. Each job should create its own dependency
injection scope when it starts.

The application must also stop safely. When ASP.NET Core begins shutting down,
the worker should stop waiting for new jobs, pass the host cancellation token to
the job currently running, and exit within a configured shutdown timeout. Add
tests that prove the request returns before the slow work finishes, one failed
job does not stop the worker, queue capacity is limited, and cancellation reaches
the active job.

Finally, explain the choices in practical language: use `BackgroundService` for
this long-running queue-consumer loop, explain how it relates to
`IHostedService`, and state when scheduled or durable work should move to
Hangfire instead.

## Goal

Move deliberately slow work out of an ASP.NET Core request while keeping the
workflow observable and safe to stop. A request should validate and enqueue a
job, return `202 Accepted` immediately, and let a `BackgroundService` consume
the job with cooperative cancellation.

The implementation must also explain when a direct `IHostedService` or Hangfire
is the better choice, especially for scheduled and durable work.

## Repository analysis and implementation boundary

The maintained backend is `Day7/piece2/QuotesApi`. Days 11 and 12 already add
focused backend lessons to that project while keeping only new documentation
and evidence in their own day folders. This avoids copying a large API whose
auth, health checks, telemetry, EF Core setup, and tests have continued to
evolve.

Day 18 should follow that convention:

- Add the production code to `Day7/piece2/QuotesApi`.
- Add unit and HTTP-level tests to the existing Day 7 test projects.
- Keep the Day 18 plan, submission notes, commands, and evidence in `Day18/`.
- Update the CI solution target only if required; the current general CI still
  targets Day 5 even though Day 7 is the deployed API, a known repository gap.

Do not copy the full API into `Day18/piece2`. That would create another stale
fork and make the background-job diff difficult to review.

## Proposed use case

Add an asynchronous quote-author report workflow:

1. `POST /api/background-jobs/quote-author-reports` validates the request and
   enqueues a `QuoteAuthorReportJob` containing only immutable data and a job
   identifier.
2. The endpoint returns `202 Accepted` with a `Location` header pointing to
   `GET /api/background-jobs/{jobId}`.
3. A worker creates a fresh dependency-injection scope, runs the report
   processor, and records its state as `Queued`, `Running`, `Succeeded`,
   `Failed`, or `Cancelled`.
4. The status endpoint lets the caller observe completion without holding the
   original HTTP connection open.

The report is a repository-relevant example because it can query the existing
quotes data and model slow CPU/I/O work without changing quote or collection
behavior. The processor should be behind an interface so tests can use a
controlled fake rather than real delays.

## Architecture

### 1. Strongly typed, bounded queue

Create `IBackgroundJobQueue` backed by `System.Threading.Channels.Channel<T>`.
Use a bounded channel with capacity supplied by validated options.

Key decisions:

- Queue job records, not `Func<CancellationToken, Task>` delegates. A delegate
  can accidentally capture `HttpContext`, a scoped `DbContext`, a user token,
  or another request-owned object that is disposed when the request ends.
- Register the queue as a singleton because both request producers and the
  hosted consumer must share the same channel.
- Preserve FIFO ordering with one reader for deterministic behavior. Document
  that increasing consumer concurrency changes ordering and database pressure.
- Use `TryEnqueue` at the HTTP boundary. When the bounded queue is full, return
  `503 Service Unavailable` with a `Retry-After` header rather than allowing an
  unbounded queue to consume memory or making the request wait indefinitely.
- Do not accept a job until both its status record and queue write succeed. If
  queueing fails, remove the provisional status record.

The initial queue is intentionally in-memory. It demonstrates the hosted-service
pattern but does not claim durability: queued work is lost on process crashes,
deployments, scale-to-zero, and can be routed to different queues when multiple
replicas run.

### 2. Job state store

Create a small thread-safe `IBackgroundJobStore` implementation using a
`ConcurrentDictionary<Guid, BackgroundJobState>`.

- Store timestamps, state, result metadata, and a safe failure message.
- Make transitions explicit: `Queued -> Running -> Succeeded/Failed/Cancelled`.
- Never expose exception stack traces through the status endpoint.
- Bound retention or remove terminal entries after a configured lifetime so the
  status store cannot grow forever.

This store is for observability in the exercise, not a durable job database.
The documentation must state that a real multi-instance system needs persistent
storage or a background-job product.

### 3. Scoped job processor

Define `IQuoteAuthorReportProcessor` as a scoped service. It can resolve the
existing `QuotesDbContext`, run the report query with `AsNoTracking`, and produce
an immutable result.

The queued record must not carry an EF entity or `DbContext`. The worker uses
`IServiceScopeFactory.CreateAsyncScope()` for every job and resolves the
processor inside that scope. This gives each job its own scoped dependencies and
ensures they are disposed even after failure or cancellation.

Pass the worker cancellation token to every cancellable operation, including
channel reads, EF Core queries, artificial development-only latency, and result
storage.

### 4. BackgroundService consumer

Implement `QueuedBackgroundJobService : BackgroundService`.

The execution loop should:

1. Wait asynchronously for the next item using the host-provided
   `stoppingToken`.
2. Mark the job as running.
3. Create an async DI scope and process the item with the same token.
4. Mark success, cancellation, or failure.
5. Log structured fields such as `JobId`, `JobType`, elapsed time, and outcome.
6. Catch an individual job failure so one bad job does not terminate the worker
   and leave all later work stranded.

Only treat `OperationCanceledException` as an expected shutdown when the host
token was actually cancelled. Other cancellation or exceptions should remain
visible as failures.

### 5. Graceful shutdown contract

`BackgroundService.ExecuteAsync` receives the application stopping token. The
implementation should use it as the single shutdown signal:

- A blocked channel read exits promptly when shutdown begins.
- The currently running processor receives the same token and cooperatively
  stops database, delay, or I/O work.
- The worker starts no additional jobs after cancellation is observed.
- Expected shutdown cancellation is logged at information level, not as an
  unhandled error.
- Configure and document `HostOptions.ShutdownTimeout` so the host has a bounded
  grace period before forced termination.

With an in-memory queue, “graceful” means stopping cooperatively and predictably;
it does not mean guaranteeing completion of every accepted item. Any requirement
to survive restarts or finish all accepted jobs changes the architecture to a
durable queue or Hangfire.

### 6. HTTP endpoints and security

Add a focused endpoint extension rather than placing route logic in `Program.cs`:

- `POST /api/background-jobs/quote-author-reports`
- `GET /api/background-jobs/{jobId}`

The POST response should contain the job id, `Queued` status, and status URL.
The GET response should return `404` for an unknown id and `200` for known jobs.

Apply the existing quote-read authorization policy to both routes. Capture any
required caller id as plain immutable job data; never retain `ClaimsPrincipal`,
`HttpContext`, headers, or bearer tokens beyond the request.

Register the endpoints in `Program.cs`, but keep registrations and behavior in
dedicated extension/classes so the composition root remains readable.

## BackgroundService, IHostedService, and Hangfire

| Choice                  | Best fit                                                                                                             | Scheduling and durability                                  | Trade-off                                                                                                                                                      |
| ----------------------- | -------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `BackgroundService`     | A long-running loop such as draining a channel, polling, or consuming a broker                                       | No persistence or distributed scheduling by itself         | Minimal dependency and integrates with DI/host lifetime, but the application owns retries, status, locking, backpressure, and recovery                         |
| Direct `IHostedService` | Precise startup/shutdown coordination or a service that does not naturally have one long-running `ExecuteAsync` loop | No persistence by itself                                   | More lifecycle control, but more boilerplate; `BackgroundService` already implements `IHostedService` and is clearer for this consumer loop                    |
| Hangfire                | Durable fire-and-forget, delayed, recurring/cron jobs, retries, dashboards, and multi-server coordination            | Persists jobs in supported storage and coordinates workers | Operational dependency and storage schema, but the correct choice once accepted work must survive restarts or scheduled work must run reliably across replicas |

For a simple best-effort timer in one process, a `BackgroundService` plus
`PeriodicTimer` is sufficient. For business-critical scheduled work, use
Hangfire recurring jobs (or the platform's scheduler plus a durable queue), not
an in-process timer that resets on deploy and runs once per replica.

## Planned file changes

```text
Day7/piece2/QuotesApi/
  BackgroundJobs/
    BackgroundJobQueueOptions.cs
    BackgroundJobState.cs
    IBackgroundJobQueue.cs
    IBackgroundJobStore.cs
    InMemoryBackgroundJobQueue.cs
    InMemoryBackgroundJobStore.cs
    QuoteAuthorReportJob.cs
    QueuedBackgroundJobService.cs
  Services/
    IQuoteAuthorReportProcessor.cs
    QuoteAuthorReportProcessor.cs
  Extensions/
    BackgroundJobEndpointExtensions.cs
    InfrastructureExtensions.cs              # registrations/options validation
  Program.cs                                 # map the new endpoint group
  appsettings.json                           # queue capacity/retention/shutdown settings

Day7/piece2/Quotes.Tests.Unit/
  BackgroundJobs/
    InMemoryBackgroundJobQueueTests.cs
    QueuedBackgroundJobServiceTests.cs
    BackgroundJobStoreTests.cs

Day7/piece2/Quotes.Tests.Integration/
  BackgroundJobEndpointTests.cs

Day18/docs/
  day18-background-jobs-implementation-plan.md
  day18-background-jobs-submission.md         # added during implementation
```

Names may be consolidated if a type remains trivial, but the queue contract,
worker, processor, endpoint mapping, and tests should remain separate concerns.

## Implementation sequence

1. Add failing queue tests for FIFO dequeue, capacity rejection, and cancellation
   of a blocked read.
2. Implement the bounded channel queue and validated options.
3. Add state-store tests for legal transitions, terminal state, and retention;
   implement the in-memory store.
4. Add a fake processor and worker tests covering successful processing, fresh
   scope resolution, exception isolation, and host cancellation.
5. Implement the scoped report processor and hosted worker.
6. Register the singleton queue/store, scoped processor, hosted service, options,
   and bounded shutdown timeout.
7. Add endpoint integration tests first, then implement the POST/GET routes and
   authorization.
8. Add structured logs and verify no request-owned or secret data is logged.
9. Run formatting, build, unit tests, integration tests, and a manual shutdown
   exercise.
10. Record commands, responses, shutdown logs, limitations, and the comparison
    with `IHostedService`/Hangfire in the Day 18 submission document.

## Test strategy

### Unit tests

- Queue preserves FIFO order.
- Queue refuses new work at capacity without growing unbounded.
- Cancellation unblocks an empty dequeue.
- Worker changes state from queued to running to succeeded.
- Processor exception marks only that job failed and the next job still runs.
- Host cancellation reaches the active processor and marks it cancelled.
- Every job gets a fresh scope and disposed scoped dependencies.
- Store does not allow invalid or regressive state transitions.

Use `TaskCompletionSource` or a controlled fake processor for synchronization.
Do not use timing-sensitive `Task.Delay` assertions in automated tests.

### Integration tests

- Authenticated POST returns `202`, a job id, and a valid `Location` header.
- Anonymous POST/GET follows the existing authorization contract.
- Unknown job id returns `404`.
- Queue saturation returns `503` plus `Retry-After`.
- A queued job becomes terminal and its status response is safe and stable.
- The request completes before the controlled processor is released, proving the
  slow work is not running on the request path.

### Manual verification

1. Start the API in Development and authenticate.
2. Enqueue a deliberately blocked/slow report and record the fast `202` response.
3. Poll the status URL through `Queued`, `Running`, and `Succeeded`.
4. Start another job, stop the host while it is running, and capture logs showing
   cancellation reached the processor and the host stopped within its timeout.
5. Restart and demonstrate/document that in-memory queued status is gone.
6. Run two instances conceptually or locally to explain why in-memory scheduling
   is per replica and why Hangfire/durable messaging changes that guarantee.

## Observability

- Use structured logs for enqueue, start, finish, failure, queue-full, and
  shutdown-cancel events.
- Include job id/type and duration; exclude request authorization headers and
  exception details from API responses.
- Add queue depth and outcome counters only if the repository's current
  OpenTelemetry setup can expose them without introducing an unrelated metrics
  subsystem. Logs and status transitions are the minimum acceptance evidence.
- Preserve correlation by copying only the request trace id as an immutable
  string or creating a linked `Activity`; do not retain the request `Activity`
  object itself.

## Risks and mitigations

- **Accepted work is lost on restart:** state this explicitly; select Hangfire or
  a durable broker when completion is required.
- **Multiple replicas have isolated queues:** do not present this implementation
  as cluster-wide scheduling.
- **Queue overload:** bounded capacity plus immediate `503` backpressure.
- **Scoped dependency leakage:** queue data only; create a new async scope per job.
- **Worker death after one exception:** catch per-job exceptions and continue.
- **Shutdown hangs:** propagate the host token through every asynchronous layer
  and enforce a shutdown timeout.
- **Duplicate execution after moving to durable storage:** design processors to
  be idempotent before introducing retries.
- **Status-memory growth:** configured retention/cleanup for terminal jobs.

## Acceptance criteria

- POST returns `202 Accepted` without waiting for the report processor.
- A bounded queue is drained by a registered `BackgroundService`.
- No request-scoped service or `HttpContext` is captured in queued work.
- Each job runs in its own async DI scope.
- Queue-full behavior is deterministic and observable.
- Per-job failures do not stop the worker.
- Shutdown cancellation reaches both the channel wait and active processor, and
  the process exits within the configured grace period.
- Tests cover enqueue/dequeue, request decoupling, failure isolation, and
  cancellation without depending on wall-clock sleeps.
- Documentation accurately contrasts `BackgroundService`, direct
  `IHostedService`, and Hangfire, including the in-memory design's durability and
  multi-replica limitations.
