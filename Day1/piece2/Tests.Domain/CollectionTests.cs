using QuotesApi.Models;

namespace Tests.Domain;

public class CollectionTests
{
    [Fact]
    public void Constructor_ThrowsArgumentException_WhenNameIsEmpty()
    {
        Action act = () => new Collection("", "user-1");
        act.Should().Throw<ArgumentException>().WithMessage("*between 3 and 80*");
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenNameIsLessThan3Chars()
    {
        Action act = () => new Collection("ab", "user-1");
        act.Should().Throw<ArgumentException>().WithMessage("*between 3 and 80*");
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenNameExceeds80Chars()
    {
        var longName = new string('a', 81);
        Action act = () => new Collection(longName, "user-1");
        act.Should().Throw<ArgumentException>().WithMessage("*between 3 and 80*");
    }

    [Fact]
    public void AddItem_ThrowsInvalidOperationException_WhenCollectionHas50Items()
    {
        var collection = new Collection("Test", "user-1");
        for (int i = 1; i <= 50; i++)
            collection.AddItem(i, DateTimeOffset.UtcNow);

        Action act = () => collection.AddItem(51, DateTimeOffset.UtcNow);
        act.Should().Throw<InvalidOperationException>().WithMessage("*50*");
    }

    [Fact]
    public void AddItem_ThrowsInvalidOperationException_WhenQuoteAlreadyExists()
    {
        var collection = new Collection("Test", "user-1");
        collection.AddItem(1, DateTimeOffset.UtcNow);

        Action act = () => collection.AddItem(1, DateTimeOffset.UtcNow);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already in*");
    }

    [Fact]
    public void RemoveItem_ThrowsKeyNotFoundException_WhenQuoteNotInCollection()
    {
        var collection = new Collection("Test", "user-1");

        Action act = () => collection.RemoveItem(999);
        act.Should().Throw<KeyNotFoundException>().WithMessage("*not in*");
    }

    [Fact]
    public void AddItem_ThenRemoveItem_LeavesZeroItems()
    {
        var collection = new Collection("Test", "user-1");
        collection.AddItem(1, DateTimeOffset.UtcNow);
        collection.RemoveItem(1);

        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void AddMultipleItems_ThenRemoveOneLeavesSomeItems()
    {
        var collection = new Collection("Test", "user-1");
        collection.AddItem(1, DateTimeOffset.UtcNow);
        collection.AddItem(2, DateTimeOffset.UtcNow);
        collection.RemoveItem(1);

        collection.Items.Should().HaveCount(1);
        collection.Items.First().QuoteId.Should().Be(2);
    }

    [Fact]
    public void Constructor_AllowsValidNameBetween3And80Chars()
    {
        var validName = "My Favorite Quotes";
        var collection = new Collection(validName, "user-1");

        collection.Name.Should().Be(validName);
        collection.OwnerId.Should().Be("user-1");
        collection.Items.Should().BeEmpty();
    }
}
