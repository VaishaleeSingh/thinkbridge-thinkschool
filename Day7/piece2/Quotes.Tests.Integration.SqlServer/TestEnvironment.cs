using System.Runtime.CompilerServices;

namespace Quotes.Tests.Integration.SqlServer;

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
}
