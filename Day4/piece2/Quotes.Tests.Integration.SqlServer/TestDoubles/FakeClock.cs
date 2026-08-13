using QuotesApi.Services;

namespace Quotes.Tests.Integration.SqlServer.TestDoubles;

/// <summary>
/// Same idea as the FakeClock in Quotes.Tests.Integration (and in
/// Quotes.Tests.Unit before that), kept as its own copy here rather than
/// a project reference to either -- these test projects test the app at
/// different levels and against different database engines, and
/// shouldn't need to know about each other to compile.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;
}
