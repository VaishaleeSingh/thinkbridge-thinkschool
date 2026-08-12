using System.Text.RegularExpressions;

namespace QuotesApi.Services;

public sealed partial class QuoteTextNormalizer : IQuoteTextNormalizer
{
    public string Normalize(string value)
    {
        var trimmed = value.Trim();
        return WhitespaceRun().Replace(trimmed, " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
