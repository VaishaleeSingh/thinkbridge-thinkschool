namespace QuotesApi.Models;

/// <summary>
/// What a collection looks like to a caller listing them: the collection
/// itself plus the actual quotes in it, not just the ids.
///
/// The ids alone are useless to a client -- nobody can render a collection
/// from a list of integers, so every consumer would immediately call back
/// for each quote. Resolving them server-side is the right shape; the
/// interesting question is how many queries it costs, which is what the
/// trace in docs/slow-endpoint-diagnosis.md is about.
/// </summary>
public sealed record CollectionWithQuotes(
    int Id,
    string Name,
    IReadOnlyList<QuoteSummary> Quotes);

public sealed record QuoteSummary(int Id, string Author, string Text);
