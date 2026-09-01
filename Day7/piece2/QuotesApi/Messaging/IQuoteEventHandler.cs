namespace QuotesApi.Messaging;

/// <summary>
/// Handles one processed <see cref="QuoteChangedEvent"/> for a specific subscription.
/// Implementations must be idempotent: the same event may be delivered more than
/// once. The dedupe guarantee comes from <see cref="IProcessedMessageStore"/>,
/// not from this interface.
///
/// A handler does not name its own subscription. It is registered under the
/// configured subscription name as a keyed service, and that registration is
/// the single place the association lives -- a SubscriptionName property here
/// would be a second one, free to disagree with it.
/// </summary>
public interface IQuoteEventHandler
{
    Task HandleAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default);
}
