using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.BackgroundJobs;
using QuotesApi.Services;

namespace Quotes.Tests.Unit.BackgroundJobs;

public class QueuedBackgroundJobServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenOneJobFails_ProcessesTheNextJob()
    {
        var queue = CreateQueue();
        var store = CreateStore();
        var processor = new FailFirstProcessor();
        await using var provider = BuildProvider(processor);
        var worker = CreateWorker(queue, store, provider);
        var first = CreateJob();
        var second = CreateJob();

        store.TryCreate(first).Should().BeTrue();
        store.TryCreate(second).Should().BeTrue();
        queue.TryEnqueue(first).Should().BeTrue();
        queue.TryEnqueue(second).Should().BeTrue();

        await worker.StartAsync(CancellationToken.None);
        await processor.SecondJobCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopWorkerAsync(worker);

        store.TryGet(first.Id, out var failed).Should().BeTrue();
        store.TryGet(second.Id, out var succeeded).Should().BeTrue();
        failed!.Status.Should().Be(BackgroundJobStatus.Failed);
        succeeded!.Status.Should().Be(BackgroundJobStatus.Succeeded);
    }

    [Fact]
    public async Task StopAsync_CancelsTheActiveProcessorAndMarksTheJobCancelled()
    {
        var queue = CreateQueue();
        var store = CreateStore();
        var processor = new BlockingProcessor();
        await using var provider = BuildProvider(processor);
        var worker = CreateWorker(queue, store, provider);
        var job = CreateJob();

        store.TryCreate(job).Should().BeTrue();
        queue.TryEnqueue(job).Should().BeTrue();

        await worker.StartAsync(CancellationToken.None);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopWorkerAsync(worker);

        await processor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.TryGet(job.Id, out var cancelled).Should().BeTrue();
        cancelled!.Status.Should().Be(BackgroundJobStatus.Cancelled);
    }

    private static InMemoryBackgroundJobQueue CreateQueue() =>
        new(Options.Create(new BackgroundJobQueueOptions
        {
            QueueCapacity = 10
        }));

    private static InMemoryBackgroundJobStore CreateStore() =>
        new(
            new FakeClock(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero)),
            Options.Create(new BackgroundJobQueueOptions()));

    private static ServiceProvider BuildProvider(IQuoteAuthorReportProcessor processor)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => processor);
        return services.BuildServiceProvider();
    }

    private static QueuedBackgroundJobService CreateWorker(
        IBackgroundJobQueue queue,
        IBackgroundJobStore store,
        ServiceProvider provider) =>
        new(
            queue,
            store,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueuedBackgroundJobService>.Instance);

    private static async Task StopWorkerAsync(QueuedBackgroundJobService worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);
        worker.Dispose();
    }

    private static QuoteAuthorReportJob CreateJob() =>
        new(Guid.NewGuid(), 10, "test-user");

    private static QuoteAuthorReportResult EmptyResult() =>
        new(0, 0, []);

    private sealed class FailFirstProcessor : IQuoteAuthorReportProcessor
    {
        private int _attempts;

        public TaskCompletionSource SecondJobCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<QuoteAuthorReportResult> ProcessAsync(
            QuoteAuthorReportJob job,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                return Task.FromException<QuoteAuthorReportResult>(
                    new InvalidOperationException("Expected test failure."));
            }

            SecondJobCompleted.TrySetResult();
            return Task.FromResult(EmptyResult());
        }
    }

    private sealed class BlockingProcessor : IQuoteAuthorReportProcessor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuoteAuthorReportResult> ProcessAsync(
            QuoteAuthorReportJob job,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return EmptyResult();
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }
}
