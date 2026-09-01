using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.MsSql;

namespace Quotes.Tests.Integration.ServiceBus;

/// <summary>
/// Collection fixture: a SQL Server container (the emulator's backing store)
/// and the Azure Service Bus emulator container, on one Docker network.
///
/// Lifetime: one pair of containers per [Collection], started before the first
/// test and disposed after the last. Two containers take 10-30s on a warm
/// Docker daemon and considerably longer on the first image pull, so sharing
/// them across the suite is what keeps this runnable in a feedback loop.
///
/// THE NETWORK IS NOT OPTIONAL. The emulator connects to SQL Server from
/// INSIDE its own container, so SQL_SERVER must be a name that resolves on a
/// network both containers are attached to. Handing it the host-side hostname
/// ("localhost") points the emulator at itself and it never starts. Hence an
/// explicit network, a network alias on the SQL container, and that same alias
/// in SQL_SERVER.
///
/// TRANSPORT: the emulator speaks AMQP over TCP only -- the WebSockets
/// transport is documented as unsupported. A client configured for it will
/// not connect, so tests leave ServiceBusClientOptions.TransportType at its
/// default.
///
/// The namespace name in emulator-config.json is "sbemulatorns" and cannot be
/// changed: the emulator hosts exactly one namespace and its preset name is
/// not renameable.
///
/// Neither the SA password nor the emulator's shared access key below is a
/// secret. The emulator ships with one well-known key, exactly as the SQL
/// Server test container ships with a password chosen by the test; both exist
/// only inside a container that lives for the length of this test run.
/// </summary>
public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    private const string EmulatorImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";
    private const string SaPassword = "Th!nkSch001Test#";
    private const string SqlAlias = "sql-server-backing";
    private const int AmqpPort = 5672;
    private const int MgmtPort = 5300;

    private readonly INetwork _network;
    private readonly MsSqlContainer _sqlServer;
    private IContainer? _emulator;

    public ServiceBusEmulatorFixture()
    {
        _network = new NetworkBuilder()
            .WithName($"sb-test-{Guid.NewGuid():N}")
            .Build();

        _sqlServer = new MsSqlBuilder()
            .WithImage(SqlServerImage)
            .WithPassword(SaPassword)
            .WithNetwork(_network)
            .WithNetworkAliases(SqlAlias)
            .Build();
    }

    /// <summary>
    /// The emulator's connection string. "SAS_KEY_VALUE" is the literal,
    /// documented emulator key, not a placeholder left unfilled;
    /// UseDevelopmentEmulator=true is what tells the SDK to skip TLS.
    /// </summary>
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();

        // SQL Server first: the emulator probes it during its own startup.
        await _sqlServer.StartAsync();

        var configPath = Path.Combine(AppContext.BaseDirectory, "emulator-config.json");

        _emulator = new ContainerBuilder()
            .WithImage(EmulatorImage)
            .WithNetwork(_network)
            .WithPortBinding(AmqpPort, assignRandomHostPort: true)
            .WithPortBinding(MgmtPort, assignRandomHostPort: true)
            .WithEnvironment("ACCEPT_EULA", "Y")
            // Resolved over the shared network, from inside the container.
            .WithEnvironment("SQL_SERVER", SqlAlias)
            .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
            .WithBindMount(configPath, "/ServiceBus_Emulator/ConfigFiles/Config.json")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r
                        .ForPath("/health")
                        .ForPort(MgmtPort)
                        .ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

        await _emulator.StartAsync();

        var amqpPort = _emulator.GetMappedPublicPort(AmqpPort);

        ConnectionString =
            $"Endpoint=sb://localhost:{amqpPort};" +
            "SharedAccessKeyName=RootManageSharedAccessKey;" +
            "SharedAccessKey=SAS_KEY_VALUE;" +
            "UseDevelopmentEmulator=true;";
    }

    public async Task DisposeAsync()
    {
        if (_emulator is not null)
            await _emulator.DisposeAsync();

        await _sqlServer.DisposeAsync();
        await _network.DeleteAsync();
    }
}

[CollectionDefinition("ServiceBusEmulator")]
public class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture> { }
