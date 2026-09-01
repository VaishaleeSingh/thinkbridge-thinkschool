using Azure.Messaging.ServiceBus;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
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
/// THE API HOST LIVES HERE TOO, and that is not incidental. It was originally
/// built per test, which quietly broke every assertion: xUnit builds a new test
/// class instance per test, so several hosts ended up running at once, each
/// with its own SQLite file but all consuming the SAME two subscriptions. A
/// message published by one test was then free to be handled by another test's
/// worker and written to a database the asserting test never reads -- the
/// competing-consumer behaviour the app is supposed to have, turned against the
/// suite. (xUnit v2 also never calls IAsyncDisposable on a test class, only
/// IDisposable or IAsyncLifetime, so those hosts were not even being shut down.)
/// One host, one database, one set of consumers, for the whole collection.
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
    private readonly string _dbFile = $"sb-test-{Guid.NewGuid():N}.db";

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

    /// <summary>
    /// The one API host for this collection: messaging on, pointed at the
    /// emulator, backed by one SQLite file. Its two workers are the only
    /// consumers of the emulator's subscriptions during the run.
    /// </summary>
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

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

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            // FullyQualifiedNamespace has to be set even though the client is
            // replaced below: ServiceBusOptions validates it on start when
            // Enabled is true, and ValidateOnStart means the host refuses to
            // boot without it.
            builder.UseSetting("ServiceBus:Enabled", "true");
            builder.UseSetting("ServiceBus:FullyQualifiedNamespace", "localhost");
            builder.UseSetting("ServiceBus:TopicName", "quote-events");
            builder.UseSetting("ServiceBus:AuditSubscription", "audit");
            builder.UseSetting("ServiceBus:SearchIndexSubscription", "search-index");

            // One concurrent call per worker, for the test host only. The
            // production default is 4; here every handler writes to a single
            // SQLite file, and SQLite takes a write lock on the whole database.
            // Concurrent handlers would spend their time colliding on that lock,
            // failing as transient errors, abandoning, and eventually
            // dead-lettering work the test is waiting for -- a property of
            // SQLite, not of the code under test. The two workers still run
            // concurrently with each other, which is the fan-out these tests
            // are about; per-subscription concurrency is asserted against SQL
            // Server, where it belongs.
            builder.UseSetting("ServiceBus:MaxConcurrentCalls", "1");

            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<QuotesDbContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);

                services.AddDbContext<QuotesDbContext>(options =>
                    options.UseSqlite($"Data Source={_dbFile}"));

                // Transport stays at the default (AMQP over TCP): the emulator
                // does not support the WebSockets transport.
                foreach (var descriptor in services
                             .Where(d => d.ServiceType == typeof(ServiceBusClient))
                             .ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton(new ServiceBusClient(ConnectionString));
            });
        });

        // Build the host (and start its workers) before any test publishes.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            using (var scope = Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
                await db.Database.EnsureDeletedAsync();
            }

            await Factory.DisposeAsync();
        }

        if (_emulator is not null)
            await _emulator.DisposeAsync();

        await _sqlServer.DisposeAsync();
        await _network.DeleteAsync();
    }
}

[CollectionDefinition("ServiceBusEmulator")]
public class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture> { }
