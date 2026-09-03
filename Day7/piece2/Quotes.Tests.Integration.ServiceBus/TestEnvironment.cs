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
}
