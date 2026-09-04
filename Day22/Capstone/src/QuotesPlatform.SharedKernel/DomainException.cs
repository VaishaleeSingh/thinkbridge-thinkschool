namespace QuotesPlatform.SharedKernel;

/// <summary>
/// A broken invariant, as distinct from a bug or an infrastructure failure.
///
/// It exists so the Host can map "you asked for something the domain forbids"
/// to a 4xx and everything else to a 5xx, without the domain knowing what an
/// HTTP status code is.
/// </summary>
public class DomainException(string message) : Exception(message);
