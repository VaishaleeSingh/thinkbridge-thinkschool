using Azure.Messaging.ServiceBus;
using System.Diagnostics;
using System.Text.Json;

namespace QuotesApi.Messaging;

/// <summary>
/// Real publisher: sends a <see cref="QuoteChangedEvent"/> to the Service Bus
/// topic "quote-events" via a long-lived <see cref="ServiceBusSender"/>.
///
/// Design decisions called out in the plan:
///
/// 1. ServiceBusClient and ServiceBusSender are singletons (wired that way in
///    MessagingExtensions). Creating one sender per request is the classic
///    Service Bus performance bug — it tears down the AMQP link and rebuilds
///    it on every call. The SDK clients are thread-safe by design.
///
/// 2. MessageId = event.EventId, NOT Guid.NewGuid() at send time.  The SDK's
///    own retry policy can retry a failed send; if the id changes on each try
///    the broker may see two distinct messages.
///
/// 3. ApplicationProperties["eventType"] drives the subscription SQL filter.
///    Filtering on the body is not possible in Service Bus; only system props
///    and application props are addressable. Getting this wrong produces a
///    filter that silently matches everything.
///
/// 4. traceparent travels as an application property so the consumer span can
///    be a child of the HTTP request that published it. An Activity/HttpContext/
///    ClaimsPrincipal must never be placed in a message — the plan's Day 18
///    rule carries over here unchanged, and matters more because the payload
///    now leaves the process.
///
/// 5. Send failures are caught and logged at Error; they do NOT fail the HTTP
///    response that already committed the database write. The plan documents
///    this as the publish/commit gap that the transactional outbox would close.
/// </summary>
public sealed class ServiceBusQuoteEventPublisher(
    ServiceBusSender sender,
    ILogger<ServiceBusQuoteEventPublisher> logger) : IQuoteEventPublisher
{
    public async Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(evt);

            var message = new ServiceBusMessage(body)
            {
                MessageId = evt.EventId,
                ContentType = "application/json",
                CorrelationId = Activity.Current?.TraceId.ToString(),
                Subject = evt.EventType
            };

            // eventType is the property the subscription SQL filter matches on.
            // Subject alone is not addressable in a SQL filter expression.
            message.ApplicationProperties["eventType"] = evt.EventType;
            message.ApplicationProperties["schemaVersion"] = evt.SchemaVersion;

            // Carry trace context as a string, not as an Activity object.
            // The handler restores it by reading this property and starting
            // a new Activity with that parent — making the consumer span a
            // child of the request that published it.
            var traceparent = Activity.Current?.Id;
            if (traceparent is not null)
                message.ApplicationProperties["traceparent"] = traceparent;

            await sender.SendMessageAsync(message, cancellationToken);

            logger.LogInformation(
                "Published {EventType} for quote {QuoteId} with MessageId {MessageId}",
                evt.EventType, evt.QuoteId, evt.EventId);
        }
        catch (Exception ex)
        {
            // PUBLISH/COMMIT GAP: the database write already committed.
            // Failing the HTTP response here would give the caller a 500
            // even though the quote was successfully saved, which is worse
            // than the alternative. Log at Error so the gap is visible; the
            // transactional outbox pattern is the correct fix (see Day 19
            // submission notes).
            logger.LogError(
                ex,
                "Failed to publish {EventType} for quote {QuoteId} (EventId={EventId}). " +
                "The database write succeeded; this event is lost unless replayed from an outbox.",
                evt.EventType, evt.QuoteId, evt.EventId);
        }
    }
}
