# Day 18 — Background jobs

## Result

The Quotes API now accepts a quote-author report as background work instead of
holding the HTTP request open while the report is generated.

```http
POST /api/background-jobs/quote-author-reports
Authorization: Bearer <token>
Content-Type: application/json

{ "topAuthors": 10 }
```

The endpoint validates and enqueues the request, then returns immediately:

```http
HTTP/1.1 202 Accepted
Location: /api/background-jobs/4df189a9-792c-4a39-9652-b5084ea76228

{
  "jobId": "4df189a9-792c-4a39-9652-b5084ea76228",
  "status": "Queued",
  "statusUrl": "/api/background-jobs/4df189a9-792c-4a39-9652-b5084ea76228"
}
```

The caller follows the status URL and observes `Queued`, `Running`, and a
terminal state (`Succeeded`, `Failed`, or `Cancelled`). The successful response
contains the report result.

## Why the implementation lives in Day 7

`Day7/piece2/QuotesApi` is the repository's maintained backend. Days 11 and 12
already extend it while keeping their new documentation in their own day
folders. Day 18 follows that established pattern instead of copying the entire
API into another stale `piece2` tree.

The implementation reuses the API's existing authentication, quote-read policy,
EF Core context, fake-clock testing pattern, telemetry pipeline, and solution.
Only Day 18-specific documentation lives in `Day18/`.

## Design

### Bounded channel, not an unbounded list

`InMemoryBackgroundJobQueue` wraps a bounded `Channel<QuoteAuthorReportJob>`.
The singleton queue is shared by HTTP producers and one hosted consumer.

- One reader preserves FIFO processing.
- Multiple request writers are supported.
- `AllowSynchronousContinuations = false` prevents consumer work from running
  inline on the request that enqueues it.
- The HTTP endpoint uses `TryEnqueue`; a full queue returns `503 Service
Unavailable` and `Retry-After: 5`.
- Capacity is validated from `BackgroundJobs:QueueCapacity` at startup.

Backpressure is part of the API contract. An unbounded queue would only move the
overload from request latency to process memory and make shutdown less
predictable.

### Data-only job records

The channel carries a `QuoteAuthorReportJob` with a generated id, requested
report size, and caller id. It never carries:

- `HttpContext`;
- `ClaimsPrincipal`;
- authorization headers or bearer tokens;
- `QuotesDbContext` or another scoped service;
- a delegate that may have captured any of those values.

The worker creates an async dependency-injection scope for every item and
resolves `IQuoteAuthorReportProcessor` inside that scope. The processor therefore
gets a fresh scoped `QuotesDbContext`, which is disposed after that job finishes.

### Observable lifecycle

`InMemoryBackgroundJobStore` maintains immutable snapshots in a
`ConcurrentDictionary` and enforces the transition sequence:

```text
Queued -> Running -> Succeeded
                  -> Failed
                  -> Cancelled
```

Updates use compare-and-swap (`TryUpdate`), so two threads cannot both move the
same snapshot from an expected state. Terminal entries expire after the
configured retention period. Cleanup occurs opportunistically when new jobs are
created.

Status reads are protected by the existing `can-read-quotes` policy and by job
ownership. A different authenticated user receives `404`, which does not reveal
whether another user's job id exists. Failure responses contain a safe generic
message; the exception and stack trace remain in structured server logs.

### Worker failure isolation

`QueuedBackgroundJobService` derives from `BackgroundService`. Its long-running
`ExecuteAsync` loop waits on the channel and processes one item at a time.

Each item has its own exception boundary. A processor exception marks that item
`Failed`, logs the exception with its job id, and lets the loop process the next
item. Without that boundary, one bad report could fault the hosted service and
strand everything behind it.

Structured log events cover worker start/stop, job start, completion duration,
failure, cancellation, invalid state, and queue saturation through the HTTP
response.

## The BackgroundService

This is the worker used by the application. It waits without blocking a thread,
creates a fresh dependency-injection scope for each job, and keeps processing
later jobs even when one job fails.

```csharp
public sealed class QueuedBackgroundJobService(
    IBackgroundJobQueue queue,
    IBackgroundJobStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<QueuedBackgroundJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background job worker started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var job = await queue.DequeueAsync(stoppingToken);
                await ProcessJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Background job worker stopped after shutdown was requested");
        }
    }

    private async Task ProcessJobAsync(
        QuoteAuthorReportJob job,
        CancellationToken stoppingToken)
    {
        if (!store.TryMarkRunning(job.Id))
        {
            logger.LogWarning(
                "Skipped background job {JobId} because it was not queued",
                job.Id);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider
                .GetRequiredService<IQuoteAuthorReportProcessor>();

            var result = await processor.ProcessAsync(job, stoppingToken);
            store.TryMarkSucceeded(job.Id, result);

            logger.LogInformation(
                "Completed background job {JobId} in {ElapsedMilliseconds} ms",
                job.Id,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            store.TryMarkCancelled(job.Id);
            logger.LogInformation(
                "Cancelled background job {JobId} during application shutdown",
                job.Id);
        }
        catch (Exception exception)
        {
            store.TryMarkFailed(job.Id);
            logger.LogError(exception, "Background job {JobId} failed", job.Id);
        }
    }
}
```

## Graceful shutdown

ASP.NET Core supplies `stoppingToken` to `ExecuteAsync`. When the application
starts shutting down, that token is cancelled. The same token is used while
waiting for a queued item and while processing the current item, so both parts
can stop cooperatively instead of leaving the process hanging.

The clean shutdown flow is:

1. `DequeueAsync(stoppingToken)` cancels a worker blocked on an empty channel.
2. The same token reaches the scoped report processor.
3. The processor passes it to `Task.Delay` and every EF Core async query.
4. A running job becomes `Cancelled` when shutdown interrupts it.
5. Shutdown cancellation is logged as expected lifecycle information, not as an
   unhandled processing error.
6. `HostOptions.ShutdownTimeout` is configured from
   `BackgroundJobs:ShutdownTimeoutSeconds` and defaults to 15 seconds.

The worker does not begin another item once the stopping token is observed. The
filtered `OperationCanceledException` catches only cancellation caused by host
shutdown, so a genuine processing error is not accidentally hidden as a normal
stop.

For this in-memory implementation, graceful means cooperative, bounded stop; it
does not mean every accepted item is guaranteed to finish. Pending jobs and
their status disappear when the process exits. A durable requirement needs a
different architecture.

## BackgroundService vs. IHostedService vs. Hangfire

| Choice                  | Use it for                                                                                         | What it provides                                                                             | What remains your responsibility                                                                  |
| ----------------------- | -------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| `BackgroundService`     | A long-running asynchronous loop such as draining this channel, polling, or consuming a broker     | An `ExecuteAsync` abstraction integrated with host start and stop                            | Persistence, retries, backpressure, job status, idempotency, and multi-replica coordination       |
| Direct `IHostedService` | Precise startup/shutdown actions or a component that does not naturally have one long-running loop | Explicit `StartAsync` and `StopAsync` lifecycle hooks                                        | The execution task and all job infrastructure; more boilerplate for a queue consumer              |
| Hangfire                | Durable fire-and-forget, delayed, recurring/cron jobs, retries, dashboards, and multiple workers   | Persistent job storage, scheduling, retries, server coordination, and operational visibility | Running and securing storage/dashboard, retention, capacity planning, and idempotent job behavior |

`BackgroundService` already implements `IHostedService`. It is the clearer
choice here because draining a channel is exactly one long-running asynchronous
loop. Implementing `IHostedService` directly would add lifecycle plumbing but no
useful control for this use case.

**When should we use Hangfire?** Use Hangfire when jobs must survive application
restarts, retry automatically, run on a schedule, or coordinate across multiple
application instances.

For a best-effort task in one process, this queue is enough. For scheduled
business work, use Hangfire recurring jobs or a platform scheduler that publishes
to a durable broker. A `PeriodicTimer` inside `BackgroundService` is acceptable
only when missed executions on deploy and one execution per application replica
are explicitly acceptable.

## Configuration

```json
"BackgroundJobs": {
  "QueueCapacity": 100,
  "ProcessingDelaySeconds": 3,
  "StatusRetentionMinutes": 60,
  "ShutdownTimeoutSeconds": 15
}
```

`ProcessingDelaySeconds` makes the slow-work boundary visible in development and
can be set to zero. Tests replace the processor with controlled fakes and never
sleep for this configured duration.

## Files changed

```text
Day7/piece2/QuotesApi/
  BackgroundJobs/
    BackgroundJobModels.cs
    BackgroundJobQueueOptions.cs
    IBackgroundJobQueue.cs
    IBackgroundJobStore.cs
    InMemoryBackgroundJobQueue.cs
    InMemoryBackgroundJobStore.cs
    QueuedBackgroundJobService.cs
  Extensions/
    BackgroundJobEndpointExtensions.cs
    InfrastructureExtensions.cs
  Services/
    QuoteAuthorReportProcessor.cs
  Program.cs
  appsettings.json

Day7/piece2/Quotes.Tests.Unit/BackgroundJobs/
  InMemoryBackgroundJobQueueTests.cs
  InMemoryBackgroundJobStoreTests.cs
  QueuedBackgroundJobServiceTests.cs
  QuoteAuthorReportProcessorTests.cs

Day7/piece2/Quotes.Tests.Integration/
  BackgroundJobEndpointTests.cs

Day18/docs/
  day18-background-jobs-implementation-plan.md
  day18-background-jobs-submission.md
```

## Verification

The 12 new tests cover:

- FIFO queue order;
- bounded-capacity rejection;
- cancellation of a blocked dequeue;
- legal and terminal state transitions;
- terminal-status retention cleanup;
- failure isolation followed by successful processing;
- shutdown cancellation reaching the active processor;
- the real EF-backed report totals and author ranking;
- anonymous endpoint rejection;
- `202 Accepted` before the controlled processor is released;
- `Running -> Succeeded` status observation and result retrieval;
- queue saturation returning `503` plus `Retry-After`;
- status ownership returning `404` to a different authenticated user.

Release verification:

```text
QuotesApi.Tests:             23 passed
Quotes.Tests.Unit:          107 passed
Quotes.Tests.Integration:    38 passed
Total:                      168 passed
```

The full solution build succeeded. Its five pre-existing SQL Server integration
tests could not start because Docker/Testcontainers is unavailable in the local
environment; all five fail while constructing `MsSqlContainerFixture`, before
the API or any Day 18 code runs.

Restore/build also reports existing high-severity advisory warnings for
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 and, in the SQL Server Testcontainers
project, `SSH.NET` 2024.1.0. Those dependencies were not introduced or changed
by Day 18, but they should be upgraded in a dedicated dependency-maintenance
change.

## Browser evidence

Captured live against the running API on 2026-08-31; see
`Day18/verification/browser-evidence.md` and
`Day18/verification/screenshots/`:

- `202 Accepted` in 43 ms with the `Location` status URL, against ~3 s of work;
- `Queued -> Running` (t+43 ms) `-> Succeeded` (t+3509 ms) with the report result;
- `401` anonymous, `404` for a job id the caller does not own, `400` for
  `topAuthors` outside 1-100;
- 130 concurrent enqueues against capacity 100: 101 x `202`, 29 x `503` with
  `Retry-After: 5`.

Graceful shutdown is not observable over HTTP and stays covered by the unit
tests and the host's shutdown log lines.

## Production limitations and next step

This implementation is deliberately honest about being process-local:

- accepted work is lost on crash, restart, deployment, or scale-to-zero;
- each application replica has a separate queue and status store;
- retries are not automatic;
- status cleanup is opportunistic rather than a dedicated maintenance job;
- the artificial processing delay is demonstration-only;
- processing is single-consumer and deliberately trades throughput for ordering
  and predictable database pressure.

If report completion becomes a business guarantee, replace the in-memory queue
and store with Hangfire backed by persistent storage, or publish idempotent job
messages to a durable broker and run a separately scalable worker. The HTTP
contract (`202` plus status URL) and the scoped processor boundary can remain;
only the queue, status persistence, and execution host need to change.
