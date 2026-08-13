namespace QuotesApi.Services;

/// <summary>
/// Normalizes free-text input (trims, collapses internal whitespace
/// runs) before it's persisted. Stateless and cheap to construct, so
/// it's registered as Transient — a fresh instance per resolution is
/// fine because there's nothing to reuse or share.
/// </summary>
public interface IQuoteTextNormalizer
{
    string Normalize(string value);
}
