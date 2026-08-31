using FluentAssertions;
using Microsoft.Extensions.Options;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.BackgroundJobs;

namespace Quotes.Tests.Unit.BackgroundJobs;

public class InMemoryBackgroundJobStoreTests
{
    [Fact]
    public void StateTransitions_FollowTheJobLifecycle()
    {
        var now = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(now);
        var store = CreateStore(clock);
        var job = new QuoteAuthorReportJob(Guid.NewGuid(), 10, "test-user");
        var result = new QuoteAuthorReportResult(5, 2, []);

        store.TryCreate(job).Should().BeTrue();

        clock.UtcNow = now.AddSeconds(1);
        store.TryMarkRunning(job.Id).Should().BeTrue();

        clock.UtcNow = now.AddSeconds(2);
        store.TryMarkSucceeded(job.Id, result).Should().BeTrue();
        store.TryMarkFailed(job.Id).Should().BeFalse();

        store.TryGet(job.Id, out var snapshot).Should().BeTrue();
        snapshot.Should().NotBeNull();
        snapshot!.Status.Should().Be(BackgroundJobStatus.Succeeded);
        snapshot.StartedAt.Should().Be(now.AddSeconds(1));
        snapshot.CompletedAt.Should().Be(now.AddSeconds(2));
        snapshot.Result.Should().Be(result);
    }

    [Fact]
    public void TryCreate_RemovesExpiredTerminalJobs()
    {
        var now = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(now);
        var store = CreateStore(clock, retentionMinutes: 1);
        var oldJob = new QuoteAuthorReportJob(Guid.NewGuid(), 10, "test-user");

        store.TryCreate(oldJob).Should().BeTrue();
        store.TryMarkRunning(oldJob.Id).Should().BeTrue();
        store.TryMarkFailed(oldJob.Id).Should().BeTrue();

        clock.UtcNow = now.AddMinutes(2);
        store.TryCreate(new QuoteAuthorReportJob(Guid.NewGuid(), 10, "test-user"))
            .Should().BeTrue();

        store.TryGet(oldJob.Id, out _).Should().BeFalse();
    }

    private static InMemoryBackgroundJobStore CreateStore(
        FakeClock clock,
        int retentionMinutes = 60) =>
        new(
            clock,
            Options.Create(new BackgroundJobQueueOptions
            {
                StatusRetentionMinutes = retentionMinutes
            }));
}
