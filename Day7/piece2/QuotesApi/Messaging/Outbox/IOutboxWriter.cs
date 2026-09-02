using QuotesApi.Models;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// Adds an outbox row to the caller's unit of work.
///
/// Note what is absent: any Save, Commit or Flush. That is the whole contract.
/// A writer that saved on its own behalf could commit the intent to publish
/// without the domain change that justifies it -- the mirror image of the bug
/// this pattern exists to remove. The caller owns the transaction; this only
/// contributes to it.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Serialises the event and stages a Pending row on the SAME DbContext the
    /// caller is using. Returns the staged entity so a test can assert on it
    /// before the transaction commits.
    /// </summary>
    OutboxMessage Enqueue(QuoteChangedEvent evt);
}
