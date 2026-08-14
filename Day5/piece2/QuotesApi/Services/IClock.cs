namespace QuotesApi.Services;

/// <summary>
/// Abstraction over "now". Anything that would otherwise call
/// DateTime.UtcNow / DateTimeOffset.UtcNow directly should take this
/// as a constructor dependency instead.
///
/// Why: code that reads the system clock directly can't be tested
/// deterministically — "assert AddedAt is roughly now, give or take
/// a few hundred milliseconds" is a flaky test waiting to happen.
/// With IClock, tests inject a FakeClock pinned to a fixed instant
/// and assert exact equality.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
