using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Messaging;

/// <summary>
/// Configuration for Azure Service Bus, bound from the "ServiceBus" section.
/// ValidateDataAnnotations() + ValidateOnStart() in MessagingExtensions.cs
/// means a misconfigured deployment fails at startup rather than silently
/// sending nothing.
///
/// Enabled defaults to false so that the existing integration test suite,
/// which boots the real Program.cs via WebApplicationFactory, does not try
/// to open an AMQP connection to a namespace that does not exist in CI.
/// Exactly the same pattern ObservabilityExtensions uses for the OTLP exporter.
/// </summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// When false the publisher is replaced by a no-op and the processor is
    /// never started. Required to be false in test / local environments that
    /// have no namespace. Must be flipped true in appsettings.Development.json
    /// or environment config when the emulator / real namespace is available.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// e.g. "quotes-dev.servicebus.windows.net" — never a connection string.
    /// Required when Enabled is true.
    /// </summary>
    [RequiredIfEnabled]
    public string? FullyQualifiedNamespace { get; set; }

    [RequiredIfEnabled]
    public string? TopicName { get; set; } = "quote-events";

    [RequiredIfEnabled]
    public string? AuditSubscription { get; set; } = "audit";

    [RequiredIfEnabled]
    public string? SearchIndexSubscription { get; set; } = "search-index";

    [Range(1, 32)]
    public int MaxConcurrentCalls { get; set; } = 4;

    [Range(0, 1000)]
    public int PrefetchCount { get; set; } = 0;

    [Range(1, 60)]
    public int MaxAutoLockRenewalMinutes { get; set; } = 5;

    // Deliberately absent: MaxDeliveryCount. It is a property of the
    // SUBSCRIPTION, set in Day19/infra/servicebus.bicep, and the broker is the
    // only thing that acts on it. An app setting of the same name reads like a
    // knob and turns nothing.

}

/// <summary>
/// Validates the annotated string is non-empty only when ServiceBus.Enabled is true.
/// A plain [Required] would reject a valid "Enabled:false" configuration that
/// correctly omits the namespace.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredIfEnabledAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (context.ObjectInstance is ServiceBusOptions opts && opts.Enabled)
        {
            if (value is null || string.IsNullOrWhiteSpace(value.ToString()))
                return new ValidationResult(
                    $"{context.DisplayName} is required when ServiceBus:Enabled is true.",
                    new[] { context.MemberName ?? context.DisplayName });
        }

        return ValidationResult.Success;
    }
}
