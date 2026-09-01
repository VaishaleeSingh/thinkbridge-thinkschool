namespace QuotesApi.Messaging;

/// <summary>
/// Publishes a domain event to Azure Service Bus topic "quote-events".
///
/// Two implementations exist:
///   ServiceBusQuoteEventPublisher — real publisher, used when ServiceBus:Enabled is true.
///   NoOpQuoteEventPublisher       — no-op, used when ServiceBus:Enabled is false
///                                   (local dev without emulator, CI, integration tests).
/// </summary>
public interface IQuoteEventPublisher
{
    Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default);
}
