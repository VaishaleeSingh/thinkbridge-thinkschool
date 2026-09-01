namespace QuotesApi.Messaging;

/// <summary>
/// Handles one processed <see cref="QuoteChangedEvent"/> for a specific subscription.
/// Implementations must be idempotent: the same event may be delivered more than
/// once. The dedupe guarantee comes from <see cref="IProcessedMessageStore"/>,
/// not from this interface.
/// </summary>
public interface IQuoteEventHandler
{
    string SubscriptionName { get; }

    Task HandleAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default);
}
