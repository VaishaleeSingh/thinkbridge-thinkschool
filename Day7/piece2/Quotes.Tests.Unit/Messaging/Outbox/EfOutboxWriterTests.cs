using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;
using System.Diagnostics;

namespace Quotes.Tests.Unit.Messaging.Outbox;

/// <summary>
/// Tests for the writer, and in particular for the one thing it must NOT do.
/// </summary>
public class EfOutboxWriterTests
{
    private static QuotesDbContext BuildContext(string name) =>
        new(new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Fact]
    public void Enqueue_stages_the_row_without_saving_it()
    {
        // The whole contract. A writer that saved on its own behalf could
        // commit the intent to publish without the domain change that
        // justifies it -- the mirror image of the bug the outbox removes.
        using var db = BuildContext(nameof(Enqueue_stages_the_row_without_saving_it));
        var writer = new EfOutboxWriter(db);

        writer.Enqueue(QuoteChangedEvent.Created(1, "owner", "A", "T", DateTimeOffset.UtcNow));

        db.ChangeTracker.Entries<OutboxMessage>().Should().HaveCount(1, "staged...");
        db.OutboxMessages.AsNoTracking().Should().BeEmpty("...but not committed");
    }

    [Fact]
    public void Enqueue_writes_the_routing_fields_as_columns_not_only_into_the_payload()
    {
        using var db = BuildContext(nameof(Enqueue_writes_the_routing_fields_as_columns_not_only_into_the_payload));
        var writer = new EfOutboxWriter(db);

        var evt = QuoteChangedEvent.Updated(9, "owner", "Author", "Text", DateTimeOffset.UtcNow);
        var row = writer.Enqueue(evt);

        // The relay sets ApplicationProperties["eventType"] from this column.
        // If it had to deserialise the body to find the routing key, a payload
        // it cannot read would become a message it cannot route.
        row.EventType.Should().Be("QuoteUpdated");
        row.SchemaVersion.Should().Be(QuoteChangedEvent.CurrentSchemaVersion);
        row.MessageId.Should().Be(evt.EventId);
        row.Status.Should().Be(OutboxStatus.Pending);
        row.Attempts.Should().Be(0);
        row.SentAtUtc.Should().BeNull();
    }

    [Fact]
    public void The_payload_round_trips_to_an_identical_event()
    {
        using var db = BuildContext(nameof(The_payload_round_trips_to_an_identical_event));
        var writer = new EfOutboxWriter(db);

        var evt = QuoteChangedEvent.Created(11, "owner-7", "Marcus Aurelius", "Waste no more time.", DateTimeOffset.UtcNow);
        var row = writer.Enqueue(evt);

        var restored = OutboxPayload.Deserialize(row.Payload);

        // The relay publishes what was stored, so anything lost here is lost
        // from the message. Records compare by value, which makes this a real
        // equality check rather than a field-by-field approximation of one.
        restored.Should().Be(evt);
    }

    [Fact]
    public void Enqueue_captures_the_current_trace_context()
    {
        using var db = BuildContext(nameof(Enqueue_captures_the_current_trace_context));
        var writer = new EfOutboxWriter(db);

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource(nameof(Enqueue_captures_the_current_trace_context));
        using var activity = source.StartActivity("request");

        var row = writer.Enqueue(QuoteChangedEvent.Created(1, "owner", "A", "T", DateTimeOffset.UtcNow));

        // Captured here, on the request's thread, because by the time the
        // relay reads this row the request's Activity is long gone. Without it
        // the trace has a hole exactly where the interesting part is.
        row.TraceParent.Should().Be(activity!.Id);
    }
}
