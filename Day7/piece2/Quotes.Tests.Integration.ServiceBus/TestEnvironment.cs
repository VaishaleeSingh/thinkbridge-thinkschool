using System.Runtime.CompilerServices;

namespace Quotes.Tests.Integration.ServiceBus;

/// <summary>
/// Day 20 -- forces the outbox relay off for every host in this assembly.
///
/// This project boots the real app through WebApplicationFactory&lt;Program&gt; in
/// ServiceBusEmulatorFixture, so it inherits the same hazard as the other test
/// projects: a test process inherits its parent shell's environment, and
/// Outbox__RelayEnabled is exactly the variable a developer exports to watch
/// the relay work locally. Exported once, it starts a relay inside this
/// collection's single host, which then drains outbox rows on its own schedule
/// underneath whatever the tests are asserting.
///
/// It was the one project missing this pin, which is the kind of gap that only
/// shows up as an intermittent failure in whichever suite runs while the
/// variable happens to be set.
///
/// A future end-to-end test that wants a live relay should turn it on
/// explicitly in the fixture's own configuration rather than by removing this
/// -- the point is that the setting is stated by the test, not inherited from
/// the terminal.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void DisableOutboxRelayInTests()
    {
        Environment.SetEnvironmentVariable("Outbox__RelayEnabled", "false");

        // Day 21 -- and the cache, for the same reason. Cache__Enabled=true in
        // the shell would make list assertions depend on what a previous test
        // cached.
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
