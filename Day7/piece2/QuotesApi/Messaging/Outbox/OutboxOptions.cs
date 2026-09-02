using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// Configuration for the outbox, bound from the "Outbox" section and validated
/// at startup the same way ServiceBusOptions is.
///
/// RelayEnabled is a SEPARATE switch from ServiceBus:Enabled, and the
/// separation is the point. The outbox row is written unconditionally -- it is
/// part of the domain transaction, not part of messaging -- so the existing
/// integration suite, which runs with ServiceBus disabled, can now assert
/// something it never could: the row is there, Pending, and nothing consumed
/// it. If the relay ran with the no-op publisher it would mark every row Sent
/// within one tick and destroy exactly the evidence those tests need.
/// </summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// Whether the relay BackgroundService runs in this process. False in
    /// tests and in any process that should write but not publish.
    /// </summary>
    public bool RelayEnabled { get; set; } = false;

    /// <summary>
    /// Fallback tick. The relay normally wakes on the signal raised after a
    /// commit; this is what makes a missed or dropped signal cost one interval
    /// rather than a lost message.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long a claim is held. Must exceed the worst-case time to publish a
    /// full batch, or a slow relay's rows are re-claimed underneath it and
    /// published twice for no reason. Sized to that number, not to a round one.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Sent rows older than this are swept, along with ProcessedMessages rows
    /// of the same age.
    ///
    /// The floor is not arbitrary: the window must exceed message TTL plus the
    /// longest plausible dead-letter dwell time. A dedupe row swept before its
    /// message can still be replayed means the replay is treated as new, and
    /// the side effect repeats -- a silent failure of a guarantee that
    /// everything else here depends on.
    /// </summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 7;

    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Oldest-pending age past which the relay logs a warning on every tick.
    ///
    /// This design trades one failure mode for another: it removes "committed
    /// change, no message", and introduces "relay is dead and every write
    /// still succeeds silently". Pending COUNT is naturally spiky and a bad
    /// alert; a row that has been pending for minutes is unambiguous.
    /// </summary>
    public TimeSpan PendingAgeWarningThreshold { get; set; } = TimeSpan.FromMinutes(5);
}
