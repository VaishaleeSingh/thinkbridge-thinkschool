namespace QuotesApi.Caching;

/// <summary>
/// Generation token held in process. Used when no distributed cache is
/// configured, which is the single-instance and test case.
///
/// Correct for one instance and honest about it: with L1 only there is nothing
/// to share a token with, so a local counter is exactly as good as a
/// distributed one. Invalidation is immediate -- there is no
/// GenerationCacheDuration window, because there is no round trip to memoise.
/// </summary>
public sealed class InMemoryCacheGeneration : ICacheGeneration
{
    private long _generation;

    public ValueTask<string> GetAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Interlocked.Read(ref _generation).ToString());

    public ValueTask BumpAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _generation);
        return ValueTask.CompletedTask;
    }
}
