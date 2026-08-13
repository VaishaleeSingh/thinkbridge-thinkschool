namespace QuotesApi.Services;

/// <summary>
/// Production implementation of IClock. Thin wrapper around the real
/// system clock — no state, safe to share across every request, so
/// it's registered as a singleton.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
