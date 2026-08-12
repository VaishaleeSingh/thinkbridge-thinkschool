using QuotesApi.Models;
using QuotesApi.Tests.Fakes;

namespace QuotesApi.Tests.Models;

public class CollectionTests
{
    [Fact]
    public void AddItem_StampsAddedAt_WithTheProvidedClockValue()
    {
        // Arrange: a clock pinned to a known instant instead of the
        // real system clock — this is the whole point of IClock.
        var fixedInstant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(fixedInstant);
        var collection = new Collection("Favorites", "user-1");

        // Act
        collection.AddItem(quoteId: 42, addedAt: clock.UtcNow);

        // Assert: exact equality, not "within a second of now". A test
        // built on DateTime.UtcNow directly can't make this assertion
        // without a tolerance and occasional flakiness.
        var item = Assert.Single(collection.Items);
        Assert.Equal(42, item.QuoteId);
        Assert.Equal(fixedInstant.UtcDateTime, item.AddedAt);
    }

    [Fact]
    public void AddItem_Throws_WhenQuoteAlreadyInCollection()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var collection = new Collection("Favorites", "user-1");
        collection.AddItem(1, clock.UtcNow);

        Assert.Throws<InvalidOperationException>(
            () => collection.AddItem(1, clock.UtcNow));
    }
}
