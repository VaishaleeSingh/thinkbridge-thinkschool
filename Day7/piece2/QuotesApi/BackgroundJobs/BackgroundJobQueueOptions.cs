using System.ComponentModel.DataAnnotations;

namespace QuotesApi.BackgroundJobs;

public sealed class BackgroundJobQueueOptions
{
    public const string SectionName = "BackgroundJobs";

    [Range(1, 10_000)]
    public int QueueCapacity { get; init; } = 100;

    [Range(0, 300)]
    public int ProcessingDelaySeconds { get; init; } = 3;

    [Range(1, 10_080)]
    public int StatusRetentionMinutes { get; init; } = 60;

    [Range(1, 300)]
    public int ShutdownTimeoutSeconds { get; init; } = 15;
}
