using FluentAssertions;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

public class QuoteTextNormalizerTests
{
    [Fact]
    public void Normalize_WithLeadingAndTrailingWhitespace_TrimsIt()
    {
        // Arrange
        var normalizer = new QuoteTextNormalizer();
        var input = "   hello world   ";

        // Act
        var result = normalizer.Normalize(input);

        // Assert
        result.Should().Be("hello world");
    }

    [Fact]
    public void Normalize_WithMultipleInternalSpaces_CollapsesToSingleSpace()
    {
        // Arrange
        var normalizer = new QuoteTextNormalizer();
        var input = "hello     world";

        // Act
        var result = normalizer.Normalize(input);

        // Assert
        result.Should().Be("hello world");
    }

    [Fact]
    public void Normalize_WithTabsAndNewlinesBetweenWords_CollapsesToSingleSpace()
    {
        // Arrange
        var normalizer = new QuoteTextNormalizer();
        var input = "hello\t\nworld";

        // Act
        var result = normalizer.Normalize(input);

        // Assert
        result.Should().Be("hello world");
    }

    [Fact]
    public void Normalize_WithEmptyString_ReturnsEmptyString()
    {
        // Arrange
        var normalizer = new QuoteTextNormalizer();
        var input = "";

        // Act
        var result = normalizer.Normalize(input);

        // Assert
        result.Should().Be("");
    }

    [Fact]
    public void Normalize_WithAlreadyCleanString_ReturnsItUnchanged()
    {
        // Arrange
        var normalizer = new QuoteTextNormalizer();
        var input = "hello world";

        // Act
        var result = normalizer.Normalize(input);

        // Assert
        result.Should().Be("hello world");
    }

    [Fact]
    public void Normalize_WithOnlyWhitespace_ReturnsEmptyString()
    {
        // Arrange
        var normalizer = new QuoteTextNormalizer();
        var input = "     ";

        // Act
        var result = normalizer.Normalize(input);

        // Assert
        result.Should().Be("");
    }
}
