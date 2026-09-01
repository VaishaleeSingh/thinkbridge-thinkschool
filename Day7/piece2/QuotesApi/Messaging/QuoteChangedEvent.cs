namespace QuotesApi.Messaging;

/// <summary>
/// Immutable contract record published to Azure Service Bus.
///
/// DELIBERATELY NOT the Quote EF entity: serialising the entity would
/// export navigation properties, lazy-loading surprises, and internal
/// columns across a boundary that is now a public contract. Only the
/// fields a consumer actually needs travel across the wire.
///
/// schemaVersion lets a consumer reject a shape it does not understand
/// rather than silently mis-parsing it. Increment it when the shape
/// changes in a breaking way and add a migration path in the handler.
/// </summary>
public sealed record QuoteChangedEvent
{
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>
    /// Deterministic event id. Computed once when the event is built and
    /// reused across any publish retries, so the SDK's own retry does not
    /// produce two messages with different MessageIds.
    ///
    /// Generated as a GUIDv5-style SHA-256 hash over (eventType, quoteId,
    /// occurredAtTicks) so that the same logical event always yields the
    /// same id. See QuoteChangedEvent.BuildEventId for the algorithm.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>"QuoteCreated" | "QuoteUpdated" | "QuoteDeleted"</summary>
    public required string EventType { get; init; }

    public required int QuoteId { get; init; }

    /// <summary>
    /// The user who made the change. Passed as an opaque string; consumers
    /// should not assume a specific format. Null when the owner is unknown.
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>UTC timestamp the change was committed.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Schema version the producer understood when it built this event.
    /// Consumers should dead-letter immediately on an unknown version
    /// rather than guessing at the shape.
    /// </summary>
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    // Fields a consumer might need. Null on QuoteDeleted (the quote is gone).
    public string? Author { get; init; }
    public string? Text { get; init; }

    // ------------------------------------------------------------------
    // Factory helpers
    // ------------------------------------------------------------------

    public static string BuildEventId(string eventType, int quoteId, DateTimeOffset occurredAt)
    {
        // Deterministic: SHA-256 the canonical string, take first 16 bytes,
        // format as a lower-hex GUID. Stable across retries in the same
        // process AND reproducible from the same inputs after a restart.
        var raw = $"{eventType}:{quoteId}:{occurredAt.UtcTicks}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return new Guid(hash[..16]).ToString("N");
    }

    public static QuoteChangedEvent Created(int quoteId, string? ownerId, string author, string text, DateTimeOffset occurredAt) =>
        Build("QuoteCreated", quoteId, ownerId, occurredAt, author, text);

    public static QuoteChangedEvent Updated(int quoteId, string? ownerId, string author, string text, DateTimeOffset occurredAt) =>
        Build("QuoteUpdated", quoteId, ownerId, occurredAt, author, text);

    public static QuoteChangedEvent Deleted(int quoteId, string? ownerId, DateTimeOffset occurredAt) =>
        Build("QuoteDeleted", quoteId, ownerId, occurredAt);

    private static QuoteChangedEvent Build(
        string eventType,
        int quoteId,
        string? ownerId,
        DateTimeOffset occurredAt,
        string? author = null,
        string? text = null)
    {
        return new QuoteChangedEvent
        {
            EventId = BuildEventId(eventType, quoteId, occurredAt),
            EventType = eventType,
            QuoteId = quoteId,
            OwnerId = ownerId,
            OccurredAt = occurredAt,
            Author = author,
            Text = text
        };
    }
}
