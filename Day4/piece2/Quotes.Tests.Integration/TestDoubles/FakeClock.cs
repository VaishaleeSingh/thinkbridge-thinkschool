using QuotesApi.Services;

namespace Quotes.Tests.Integration.TestDoubles;

/// <summary>
/// Same idea as the FakeClock in Quotes.Tests.Unit, kept as its own copy
/// here rather than a project reference to Quotes.Tests.Unit -- these two
/// test projects test the app at different levels (unit vs. full HTTP
/// pipeline) and shouldn't need to know about each other to compile.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;
}
