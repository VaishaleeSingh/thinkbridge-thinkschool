using QuotesApi.Messaging;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;

namespace Quotes.Tests.Integration.TestDoubles;

/// <summary>
/// Fails to stage the outbox row.
///
/// Used to test the direction of atomicity that is usually skipped: not
/// "the event survives a failed publish", but "the domain change does not
/// survive a failed enqueue". If the quote were still there afterwards, the
/// transaction would be decorative.
/// </summary>
public sealed class ThrowingOutboxWriter : IOutboxWriter
{
    public OutboxMessage Enqueue(QuoteChangedEvent evt) =>
        throw new OutboxWriteFailedException();
}

/// <summary>
/// A distinct exception type, deliberately NOT InvalidOperationException or
/// ArgumentException.
///
/// ExceptionHandlingMiddleware maps both of those to 400, because that is how
/// the domain reports an invariant violation -- a caller error, not a server
/// fault. A test double that threw one of them would exercise the middleware's
/// classification rather than the transaction, and a failed outbox write is
/// emphatically not the caller's fault. This lands in the middleware's default
/// branch and comes back as a 500, which is what a real staging failure would
/// do.
/// </summary>
public sealed class OutboxWriteFailedException()
    : Exception("Simulated failure staging the outbox row.");

/// <summary>
/// Throws if anything publishes at all.
///
/// Registered in place of the real publisher to assert a negative that is
/// otherwise hard to test: no code on the request path reaches the broker. A
/// POST that returns 201 with this wired is proof the publish has genuinely
/// moved off the request path rather than merely moved into another class
/// the endpoint still calls.
/// </summary>
public sealed class ExplodingQuoteEventPublisher : IQuoteEventPublisher
{
    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "A request path published directly to the broker. After Day 20 only the relay may do this.");
}
