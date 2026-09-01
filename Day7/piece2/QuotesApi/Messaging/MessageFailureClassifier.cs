using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace QuotesApi.Messaging;

/// <summary>
/// Classifies a message-processing exception as retryable or poison.
///
/// This is the decision the whole dead-lettering exercise turns on.
///   - Retryable (abandon): transient faults that may resolve on redeliver —
///     database timeouts, temporary downstream failures.
///   - Poison (dead-letter immediately): failures that repeating cannot fix —
///     malformed JSON, unknown schema version, unparse-able ids.
///     Retrying these wastes the delivery budget and delays every other message.
///
/// Keeping this in a single testable function rather than scattered across
/// catch-blocks makes the classification rule explicit, auditable and easy to
/// adjust as new failure modes are discovered.
/// </summary>
public static class MessageFailureClassifier
{
    public static bool IsPoison(Exception exception) => exception switch
    {
        // Malformed JSON body — parsing will never succeed, retrying is pointless.
        JsonException => true,

        // Unknown schema version detected by the handler — same reasoning.
        UnknownSchemaVersionException => true,

        // Format/parse failures on identifiers (e.g. non-integer quote id).
        FormatException => true,

        // DB concurrency / connection issues — transient, should be retried.
        DbUpdateConcurrencyException => false,
        DbUpdateException => false,

        // Cancellation during shutdown — do not dead-letter, message will be
        // redelivered to another instance.
        OperationCanceledException => false,

        // Default: unknown exceptions are treated as potentially transient.
        // If they recur, MaxDeliveryCount will eventually dead-letter them.
        _ => false
    };

    public static string PoisonReason(Exception exception) => exception switch
    {
        JsonException => "InvalidPayload",
        UnknownSchemaVersionException => "UnknownSchemaVersion",
        FormatException => "InvalidFormat",
        _ => "NonRetryableError"
    };

    public static string PoisonDescription(Exception exception) =>
        // Safe to log: exception message does not carry user data or secrets
        // for the exception types classified as poison above. If a new type
        // is added here that might carry PII, sanitize before including it.
        $"{exception.GetType().Name}: {exception.Message}";
}

/// <summary>
/// Thrown by a handler when it encounters a schemaVersion it does not know
/// how to process. Immediately dead-lettered rather than retried.
/// </summary>
public sealed class UnknownSchemaVersionException(string version)
    : Exception($"Unsupported schemaVersion '{version}'. Expected '{QuoteChangedEvent.CurrentSchemaVersion}'.")
{
    public string Version { get; } = version;
}
