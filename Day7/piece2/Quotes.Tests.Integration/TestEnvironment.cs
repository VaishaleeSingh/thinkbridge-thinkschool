using System.Runtime.CompilerServices;

namespace Quotes.Tests.Integration;

/// <summary>
/// Supplies the JWT signing key that the app now validates at startup.
///
/// Jwt:Secret deliberately does NOT live in appsettings.json any more -- a
/// signing key in a committed file is a signing key everyone with repository
/// access owns. That leaves the test host with nothing to boot from, because
/// AddInfrastructure validates the options with ValidateOnStart and refuses
/// to start without one.
///
/// Setting it as an ENVIRONMENT VARIABLE is the natural fix, and not just a
/// convenient one: environment variables sit at the top of ASP.NET Core's
/// configuration precedence, above appsettings.{Environment}.json and
/// appsettings.json, which is exactly how a deployed environment injects a
/// Key Vault reference. The tests therefore exercise the same mechanism
/// production uses rather than a test-only side door. The double underscore
/// is the environment-variable spelling of the ':' separator.
///
/// A ModuleInitializer runs once when this test assembly is loaded, before
/// any test or any WebApplicationFactory host is constructed -- so every
/// host in this assembly sees it, without each test class having to
/// remember.
///
/// This value is a throwaway used only by tests. Nothing signed with it is
/// trusted anywhere.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void SetTestOnlySigningKey()
    {
        Environment.SetEnvironmentVariable(
            "Jwt__Secret",
            "integration-test-only-signing-key-not-used-anywhere-real");
    }

    /// <summary>
    /// Day 20 -- forces the outbox relay off for every host in this assembly.
    ///
    /// A test process INHERITS its parent shell's environment, and
    /// Outbox__RelayEnabled is exactly the variable a developer exports to
    /// watch the relay work locally. Exported once, it then starts a relay
    /// inside every WebApplicationFactory host in this run: outbox rows are
    /// drained before the assertions read them, and against this project's
    /// single shared in-memory SQLite connection the relay's background
    /// queries collide with the test's own, failing as "SQLite Error 5: not an
    /// error" and "unable to delete/modify user-function due to active
    /// statements" in tests that have nothing to do with messaging.
    ///
    /// Cleared here rather than defended against in each factory, because a
    /// factory added later would not know to. Setting it to "false" rather
    /// than removing it also beats any appsettings value, which is what makes
    /// this a guarantee instead of a default.
    ///
    /// The relay is exercised deliberately, a pass at a time, by the tests
    /// that mean to -- see OutboxCrashRecoveryTests.
    /// </summary>
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
