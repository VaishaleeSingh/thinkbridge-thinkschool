using System.Runtime.CompilerServices;

namespace Quotes.Tests.Integration.Redis;

/// <summary>
/// Supplies the JWT signing key the app validates at startup, and pins the two
/// switches that must not be inherited from the shell.
///
/// Same reasoning as every other test project here: a test process inherits its
/// parent's environment, so a developer who exported Cache__Enabled=true or
/// Outbox__RelayEnabled=true to try something locally would otherwise change
/// what these tests are testing. This suite turns the cache on deliberately, in
/// DI, inside its own factory -- never by inheriting a variable.
///
/// This value is a throwaway used only by tests. Nothing signed with it is
/// trusted anywhere.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void SetTestOnlyDefaults()
    {
        Environment.SetEnvironmentVariable(
            "Jwt__Secret",
            "redis-integration-test-only-signing-key-not-used-anywhere-real");

        Environment.SetEnvironmentVariable("Outbox__RelayEnabled", "false");
        Environment.SetEnvironmentVariable("Cache__Enabled", "false");
    }

    /// <summary>
    /// Day 22 -- pins the resilience policy to its configured defaults for
    /// every host in this assembly.
    ///
    /// Same class of bug as Outbox__RelayEnabled and Cache__Enabled above, and
    /// the reason it is worth pre-empting rather than discovering: the Day 22
    /// circuit-breaker tests work by driving a known number of failures
    /// through a breaker with a known MinimumThroughput. A developer who
    /// exported Resilience__CircuitBreaker__MinimumThroughput=2 to watch the
    /// breaker trip locally, and then ran the suite in the same shell, would
    /// get failures whose cause is invisible: the assertions would be correct,
    /// the policy underneath them would not be the one they describe.
    ///
    /// Environment variables sit above appsettings.json in configuration
    /// precedence, so REMOVING them (null) is what hands the decision back to
    /// the options defaults rather than to whatever the shell happened to
    /// hold.
    /// </summary>
    [ModuleInitializer]
    internal static void PinResiliencePolicyInTests()
    {
        foreach (var key in new[]
                 {
                     "Resilience__TotalTimeout",
                     "Resilience__AttemptTimeout",
                     "Resilience__Retry__MaxAttempts",
                     "Resilience__Retry__BaseDelay",
                     "Resilience__Retry__IdempotentOnly",
                     "Resilience__CircuitBreaker__FailureRatio",
                     "Resilience__CircuitBreaker__MinimumThroughput",
                     "Resilience__CircuitBreaker__SamplingDuration",
                     "Resilience__CircuitBreaker__BreakDuration",
                     "Resilience__Bulkhead__PermitLimit",
                     "Resilience__Bulkhead__QueueLimit"
                 })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
