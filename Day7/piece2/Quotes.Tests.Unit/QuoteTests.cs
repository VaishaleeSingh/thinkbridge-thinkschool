using FluentAssertions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class QuoteTests
{
    [Fact]
    public void Create_WithValidAuthorAndText_ReturnsQuoteWithThoseValues()
    {
        // Arrange
        var author = "Marcus Aurelius";
        var text = "You have power over your mind, not outside events.";

        // Act
        var quote = Quote.Create(author, text);

        // Assert
        quote.Author.Should().Be(author);
        quote.Text.Should().Be(text);
    }

    [Fact]
    public void Create_WithNoCreatedByUserId_DefaultsToNull()
    {
        // Arrange
        var author = "Seneca";
        var text = "Luck is what happens when preparation meets opportunity.";

        // Act
        var quote = Quote.Create(author, text);

        // Assert
        quote.CreatedByUserId.Should().BeNull();
    }

    [Fact]
    public void Create_WithCreatedByUserId_StampsItOnTheQuote()
    {
        // Arrange
        var author = "Epictetus";
        var text = "It's not what happens to you, but how you react to it that matters.";
        var userId = "user-42";

        // Act
        var quote = Quote.Create(author, text, userId);

        // Assert
        quote.CreatedByUserId.Should().Be(userId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithMissingAuthor_ThrowsArgumentException(string? author)
    {
        // Arrange
        var text = "Some valid quote text.";

        // Act
        var act = () => Quote.Create(author!, text);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("author");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithMissingText_ThrowsArgumentException(string? text)
    {
        // Arrange
        var author = "Some Author";

        // Act
        var act = () => Quote.Create(author, text!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("text");
    }

    [Fact]
    public void Create_WithAuthorOver200Characters_ThrowsArgumentException()
    {
        // Arrange
        var author = new string('a', 201);
        var text = "Some valid quote text.";

        // Act
        var act = () => Quote.Create(author, text);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("author");
    }

    [Fact]
    public void Create_WithAuthorExactly200Characters_Succeeds()
    {
        // Arrange
        var author = new string('a', 200);
        var text = "Some valid quote text.";

        // Act
        var quote = Quote.Create(author, text);

        // Assert
        quote.Author.Should().HaveLength(200);
    }

    [Fact]
    public void Create_WithTextOver1000Characters_ThrowsArgumentException()
    {
        // Arrange
        var author = "Some Author";
        var text = new string('b', 1001);

        // Act
        var act = () => Quote.Create(author, text);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("text");
    }

    [Fact]
    public void Create_WithTextExactly1000Characters_Succeeds()
    {
        // Arrange
        var author = "Some Author";
        var text = new string('b', 1000);

        // Act
        var quote = Quote.Create(author, text);

        // Assert
        quote.Text.Should().HaveLength(1000);
    }
}
