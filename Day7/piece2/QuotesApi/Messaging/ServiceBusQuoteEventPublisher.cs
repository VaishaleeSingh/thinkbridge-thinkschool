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
/// 5. DAY 20 CHANGED THIS ONE. Send failures used to be caught here and
///    logged at Error, so that a broker outage would not turn a successful
///    201 into a 500. That was the least-bad choice while the publish ran on
///    the request path: the write had already committed, and the only
///    alternative was to lie to the caller about a quote that exists.
///
///    Nothing calls this from a request path any more. The only caller is
///    OutboxRelayService, which needs to know whether the send succeeded --
///    it has a durable row to retry, a retry budget, and a poison rule. A
///    publisher that swallows the exception would report success, the relay
///    would mark the row Sent, and the message would be lost by exactly the
///    mechanism built to stop that. So this now throws, and the relay decides.
/// </summary>
public sealed class ServiceBusQuoteEventPublisher(
    ServiceBusSender sender,
    ILogger<ServiceBusQuoteEventPublisher> logger) : IQuoteEventPublisher
{
    public async Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
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
        //
        // Day 20: Activity.Current here is the relay's "Outbox publish" span,
        // which was itself started with the ORIGINATING REQUEST's traceparent
        // as its parent (stored on the outbox row). So this still links the
        // consumer back to the request, across a gap of minutes and a
        // boundary of two processes -- which is the whole reason the row
        // carries a TraceParent column.
        var traceparent = Activity.Current?.Id;
        if (traceparent is not null)
            message.ApplicationProperties["traceparent"] = traceparent;

        // No try/catch. See point 5 above: the relay is the only caller and it
        // is the thing that knows how to handle a failure.
        await sender.SendMessageAsync(message, cancellationToken);

        logger.LogInformation(
            "Published {EventType} for quote {QuoteId} with MessageId {MessageId}",
            evt.EventType, evt.QuoteId, evt.EventId);
    }
}
