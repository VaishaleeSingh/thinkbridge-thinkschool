using QuotesApi.Models;

namespace Tests.Domain;

public class QuoteTests
{
    [Fact]
    public void Create_ReturnsQuote_WhenInputIsValid()
    {
        var (quote, error) = Quote.Create("Marcus Aurelius", "Waste no more time arguing what a good man should be.");

        quote.Should().NotBeNull();
        error.Should().BeNull();
        quote!.Author.Should().Be("Marcus Aurelius");
        quote.IsDeleted.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "Lorem ipsum")]
    [InlineData("Author", "")]
    public void Create_ReturnsError_WhenInputIsEmpty(string author, string text)
    {
        var (quote, error) = Quote.Create(author, text);

        quote.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_ReturnsError_WhenAuthorExceeds200Chars()
    {
        var longAuthor = new string('a', 201);
        var (quote, error) = Quote.Create(longAuthor, "valid text");

        quote.Should().BeNull();
        error.Should().Contain("200");
    }

    [Fact]
    public void Create_ReturnsError_WhenTextExceeds1000Chars()
    {
        var longText = new string('a', 1001);
        var (quote, error) = Quote.Create("Valid Author", longText);

        quote.Should().BeNull();
        error.Should().Contain("1000");
    }

    [Fact]
    public void Text_CannotBeChanged_AfterCreation()
    {
        var (quote, _) = Quote.Create("Author", "Original text");

        // Text property is read-only, so this won't compile:
        // quote.Text = "New text"; // ✗ compile error

        // Instead, the model forces soft-delete to make it "gone"
        quote!.Delete();
        quote.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void Delete_SetsIsDeletedFlag_WithoutChangingText()
    {
        var (quote, _) = Quote.Create("Author", "Original text");
        var originalText = quote!.Text;

        quote.Delete();

        quote.IsDeleted.Should().BeTrue();
        quote.Text.Should().Be(originalText);  // ← unchanged
    }
}
