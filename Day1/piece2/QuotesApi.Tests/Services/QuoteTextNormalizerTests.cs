using QuotesApi.Services;

namespace QuotesApi.Tests.Services;

public class QuoteTextNormalizerTests
{
    [Theory]
    [InlineData("  Marcus   Aurelius  ", "Marcus Aurelius")]
    [InlineData("No\n\nchanges   needed here", "No changes needed here")]
    public void Normalize_TrimsAndCollapsesWhitespace(string input, string expected)
    {
        IQuoteTextNormalizer normalizer = new QuoteTextNormalizer();

        var result = normalizer.Normalize(input);

        Assert.Equal(expected, result);
    }
}
