using System.Text.Json;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// The single place an outbox payload is turned into text and back.
///
/// It exists because the writer and the relay must agree exactly, and they are
/// separated by a database row and possibly by hours. Two call sites each
/// calling JsonSerializer with their own options is a contract that only holds
/// while nobody edits either one -- and a mismatch would not fail at build
/// time or at write time. It would fail at publish time, on a row that is
/// already committed, and the relay would classify its own serialiser
/// disagreement as a poison message and park a perfectly good event.
///
/// The options are deliberately the library defaults: nothing here is tuned,
/// so there is nothing to keep in sync.
/// </summary>
public static class OutboxPayload
{
    private static readonly JsonSerializerOptions Options = new();

    public static string Serialize(QuoteChangedEvent evt) =>
        JsonSerializer.Serialize(evt, Options);

    /// <summary>
    /// Throws <see cref="JsonException"/> on a body this build cannot read --
    /// including a null result, which System.Text.Json returns for the literal
    /// "null" rather than treating as an error. The relay relies on that
    /// exception type: MessageFailureClassifier calls it poison, so the row is
    /// parked on the first attempt instead of retried five times and then
    /// parked anyway.
    /// </summary>
    public static QuoteChangedEvent Deserialize(string payload) =>
        JsonSerializer.Deserialize<QuoteChangedEvent>(payload, Options)
        ?? throw new JsonException("Outbox payload deserialised to null.");
}
