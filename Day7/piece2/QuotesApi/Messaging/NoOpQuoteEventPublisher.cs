namespace QuotesApi.Messaging;

/// <summary>
/// No-op publisher used when ServiceBus:Enabled is false.
///
/// This is not a stub or a test fake: it is a production-registered
/// implementation that is intentionally registered when the namespace is not
/// configured. Its job is to keep the rest of the code ignorant of whether
/// messaging is on or off, and to make every integration test pass without
/// needing a Service Bus namespace.
/// </summary>
public sealed class NoOpQuoteEventPublisher(
    ILogger<NoOpQuoteEventPublisher> logger) : IQuoteEventPublisher
{
    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "ServiceBus is disabled. Skipping publish of {EventType} for quote {QuoteId}.",
            evt.EventType, evt.QuoteId);

        return Task.CompletedTask;
    }
}
