using FluentAssertions;
using QuotesApi.Messaging;

namespace Quotes.Tests.Unit.Messaging;

/// <summary>
/// Tests for <see cref="QuoteChangedEvent"/> factory methods and deterministic
/// event ID generation. No broker, no I/O.
/// </summary>
public class QuoteEventPublisherTests
{
    [Fact]
    public void Created_Sets_CorrectEventType()
    {
        var evt = QuoteChangedEvent.Created(1, "user1", "Author", "Text", DateTimeOffset.UtcNow);
        evt.EventType.Should().Be("QuoteCreated");
    }

    [Fact]
    public void Updated_Sets_CorrectEventType()
    {
        var evt = QuoteChangedEvent.Updated(1, "user1", "Author", "Text", DateTimeOffset.UtcNow);
        evt.EventType.Should().Be("QuoteUpdated");
    }

    [Fact]
    public void Deleted_Sets_CorrectEventType()
    {
        var evt = QuoteChangedEvent.Deleted(1, "user1", DateTimeOffset.UtcNow);
        evt.EventType.Should().Be("QuoteDeleted");
    }

    [Fact]
    public void Same_Inputs_Produce_Same_EventId()
    {
        // The MessageId must be stable across retries so the SDK's retry
        // policy does not produce two distinct message IDs.
        var occurredAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var id1 = QuoteChangedEvent.BuildEventId("QuoteCreated", 42, occurredAt);
        var id2 = QuoteChangedEvent.BuildEventId("QuoteCreated", 42, occurredAt);

        id1.Should().Be(id2);
    }

    [Fact]
    public void Different_EventTypes_Produce_Different_EventIds()
    {
        var occurredAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var createId = QuoteChangedEvent.BuildEventId("QuoteCreated", 42, occurredAt);
        var updateId = QuoteChangedEvent.BuildEventId("QuoteUpdated", 42, occurredAt);

        createId.Should().NotBe(updateId);
    }

    [Fact]
    public void Different_QuoteIds_Produce_Different_EventIds()
    {
        var occurredAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var id1 = QuoteChangedEvent.BuildEventId("QuoteCreated", 1, occurredAt);
        var id2 = QuoteChangedEvent.BuildEventId("QuoteCreated", 2, occurredAt);

        id1.Should().NotBe(id2);
    }

    [Fact]
    public void SchemaVersion_Is_Set_On_Created_Event()
    {
        var evt = QuoteChangedEvent.Created(1, null, "A", "T", DateTimeOffset.UtcNow);
        evt.SchemaVersion.Should().Be(QuoteChangedEvent.CurrentSchemaVersion);
    }

    [Fact]
    public void Deleted_Event_Has_No_Author_Or_Text()
    {
        // A consumer receives a delete event; the quote is gone from the
        // database. The event must carry only what it can: the id and owner.
        var evt = QuoteChangedEvent.Deleted(5, "owner", DateTimeOffset.UtcNow);
        evt.Author.Should().BeNull();
        evt.Text.Should().BeNull();
    }

    [Fact]
    public void EventId_Is_Set_On_Factory_Created_Events()
    {
        var evt = QuoteChangedEvent.Created(1, "u", "A", "T", DateTimeOffset.UtcNow);
        evt.EventId.Should().NotBeNullOrWhiteSpace();
    }
}
