using FluentAssertions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class CollectionTests
{
    [Fact]
    public void Constructor_WithValidNameAndOwner_CreatesCollection()
    {
        // Arrange
        var name = "My Favorites";
        var ownerId = "user-1";

        // Act
        var collection = new Collection(name, ownerId);

        // Assert
        collection.Name.Should().Be(name);
        collection.OwnerId.Should().Be(ownerId);
        collection.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithMissingName_ThrowsArgumentException(string? name)
    {
        // Arrange
        var ownerId = "user-1";

        // Act
        var act = () => new Collection(name!, ownerId);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithNameShorterThan3Characters_ThrowsArgumentException()
    {
        // Arrange
        var name = "ab";
        var ownerId = "user-1";

        // Act
        var act = () => new Collection(name, ownerId);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithNameExactly3Characters_Succeeds()
    {
        // Arrange
        var name = "abc";
        var ownerId = "user-1";

        // Act
        var collection = new Collection(name, ownerId);

        // Assert
        collection.Name.Should().Be(name);
    }

    [Fact]
    public void Constructor_WithNameLongerThan80Characters_ThrowsArgumentException()
    {
        // Arrange
        var name = new string('a', 81);
        var ownerId = "user-1";

        // Act
        var act = () => new Collection(name, ownerId);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithNameExactly80Characters_Succeeds()
    {
        // Arrange
        var name = new string('a', 80);
        var ownerId = "user-1";

        // Act
        var collection = new Collection(name, ownerId);

        // Assert
        collection.Name.Should().HaveLength(80);
    }

    [Fact]
    public void AddItem_ToEmptyCollection_AddsTheItem()
    {
        // Arrange
        var collection = new Collection("My Favorites", "user-1");
        var addedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        collection.AddItem(quoteId: 5, addedAt);

        // Assert
        collection.Items.Should().ContainSingle(x => x.QuoteId == 5);
    }

    [Fact]
    public void AddItem_WithDuplicateQuoteId_ThrowsInvalidOperationException()
    {
        // Arrange
        var collection = new Collection("My Favorites", "user-1");
        var addedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        collection.AddItem(quoteId: 5, addedAt);

        // Act
        var act = () => collection.AddItem(quoteId: 5, addedAt);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddItem_WhenCollectionAlreadyHas50Items_ThrowsInvalidOperationException()
    {
        // Arrange
        var collection = new Collection("My Favorites", "user-1");
        var addedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var quoteId = 1; quoteId <= 50; quoteId++)
            collection.AddItem(quoteId, addedAt);

        // Act
        var act = () => collection.AddItem(quoteId: 51, addedAt);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RemoveItem_WhenQuoteIsInCollection_RemovesIt()
    {
        // Arrange
        var collection = new Collection("My Favorites", "user-1");
        var addedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        collection.AddItem(quoteId: 5, addedAt);

        // Act
        collection.RemoveItem(quoteId: 5);

        // Assert
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void RemoveItem_WhenQuoteIsNotInCollection_ThrowsKeyNotFoundException()
    {
        // Arrange
        var collection = new Collection("My Favorites", "user-1");

        // Act
        var act = () => collection.RemoveItem(quoteId: 999);

        // Assert
        act.Should().Throw<KeyNotFoundException>();
    }
}
