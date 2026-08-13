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
}
