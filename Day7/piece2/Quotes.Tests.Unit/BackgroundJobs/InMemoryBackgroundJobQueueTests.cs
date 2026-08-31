using FluentAssertions;
using Microsoft.Extensions.Options;
using QuotesApi.BackgroundJobs;

namespace Quotes.Tests.Unit.BackgroundJobs;

public class InMemoryBackgroundJobQueueTests
{
    [Fact]
    public async Task DequeueAsync_ReturnsJobsInFifoOrder()
    {
        var queue = CreateQueue(capacity: 2);
        var first = CreateJob();
        var second = CreateJob();

        queue.TryEnqueue(first).Should().BeTrue();
        queue.TryEnqueue(second).Should().BeTrue();

        (await queue.DequeueAsync(CancellationToken.None)).Should().Be(first);
        (await queue.DequeueAsync(CancellationToken.None)).Should().Be(second);
    }

    [Fact]
    public void TryEnqueue_WhenQueueIsFull_ReturnsFalse()
    {
        var queue = CreateQueue(capacity: 1);

        queue.TryEnqueue(CreateJob()).Should().BeTrue();
        queue.TryEnqueue(CreateJob()).Should().BeFalse();
    }

    [Fact]
    public async Task DequeueAsync_WhenShutdownIsRequested_StopsWaiting()
    {
        var queue = CreateQueue(capacity: 1);
        using var cancellation = new CancellationTokenSource();
        var dequeue = queue.DequeueAsync(cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        var action = async () => await dequeue;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static InMemoryBackgroundJobQueue CreateQueue(int capacity) =>
        new(Options.Create(new BackgroundJobQueueOptions
        {
            QueueCapacity = capacity
        }));

    private static QuoteAuthorReportJob CreateJob() =>
        new(Guid.NewGuid(), 10, "test-user");
}
