namespace QuotesPlatform.Contracts;

/// <summary>
/// A fact one module publishes for others to consume, carried through the
/// transactional outbox.
///
/// MessageId is the idempotency key, not a correlation id: consumers record it
/// in ProcessedMessages (Day 19) and a redelivery of the same MessageId is a
/// no-op. At-least-once delivery makes that mandatory rather than defensive --
/// the broker WILL redeliver, so a consumer without this is a consumer with a
/// duplicate-processing bug that has not surfaced yet.
/// </summary>
public interface IIntegrationEvent
{
    Guid MessageId { get; }

    DateTimeOffset OccurredAt { get; }
}
