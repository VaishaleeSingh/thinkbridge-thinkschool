using System.Runtime.CompilerServices;

namespace QuotesApi.Tests;

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
    /// <summary>
    /// The single definition of the key used in this test assembly. The app
    /// reads it through the environment variable below; the test helpers
    /// that mint tokens by hand reference this constant directly.
    ///
    /// One definition matters here more than usual. These tests sign tokens
    /// themselves and the app verifies them, so the two sides must agree on
    /// the key exactly -- and until this piece, three test files each held
    /// their own copy of the literal, matching a fourth copy in
    /// appsettings.json by nothing more than luck and habit. That is the
    /// same duplication JwtOptions was introduced to remove from the
    /// application; leaving it in the tests would have been a poor joke.
    /// </summary>
    internal const string SigningKey = "integration-test-only-signing-key-not-used-anywhere-real";

    [ModuleInitializer]
    internal static void SetTestOnlySigningKey()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", SigningKey);
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
    }
}
