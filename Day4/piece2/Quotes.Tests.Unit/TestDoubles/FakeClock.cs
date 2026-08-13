using QuotesApi.Services;

namespace Quotes.Tests.Unit.TestDoubles;

/// <summary>
/// A clock that never actually ticks -- it holds whatever instant a test
/// hands it, so tests can assert exact timestamps ("this token's ExpiresAt
/// is EXACTLY 7 days after fixedNow") instead of an approximate range.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;
}
