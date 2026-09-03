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
}
