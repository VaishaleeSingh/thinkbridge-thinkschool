using QuotesApi.Services;

namespace QuotesApi.Tests.Fakes;

/// <summary>
/// Test double for IClock. Pinned to whatever instant the test hands
/// it, so assertions can check exact equality instead of "close to
/// DateTime.UtcNow" with a tolerance.
/// </summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now)
    {
        UtcNow = now;
    }

    public DateTimeOffset UtcNow { get; }
}
