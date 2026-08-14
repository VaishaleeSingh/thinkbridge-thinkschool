using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quotes.Tests.Integration.SqlServer.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Services;

namespace Quotes.Tests.Integration.SqlServer;

/// <summary>
/// Same shape as QuotesApiFactory in Quotes.Tests.Integration -- boots the
/// REAL Program.cs, swaps only the database and the clock -- but backed
/// by a real, containerized SQL Server 2022 instead of in-memory SQLite.
/// That is the whole point of this project: collation, transaction
/// isolation, and datetime-precision behavior are properties of the
/// actual database engine, not of EF Core's abstraction over it, so no
/// amount of SQLite-backed testing can catch a bug in them.
///
/// EACH TEST GETS ITS OWN DATABASE, not its own container: starting SQL
/// Server itself happens once for the whole run (see
/// MsSqlContainerFixture); this factory only points at a fresh,
/// uniquely-named database on that one running instance, so tests stay
/// isolated from each other without paying container-startup cost per
/// test. Unlike SQLite's ":memory:", nothing needs to be kept open here --
/// the database lives on the server for as long as the container runs,
/// independent of any one connection's lifetime.
///
/// MIGRATIONS: Program.cs unconditionally calls db.Database.MigrateAsync()
/// on startup -- real, unmodified production code, exactly as the
/// exercise asks. For that call to succeed here, QuotesDbContext needs to
/// be pointed at a migrations assembly that actually contains
/// SQL-Server-shaped migrations, not the existing Sqlite-scaffolded ones.
/// See QuotesApi.Migrations.SqlServer's SqlServerDesignTimeDbContextFactory
/// for exactly why, and the one-time command that generates them.
/// </summary>
public class SqlServerQuotesApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public FakeClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public SqlServerQuotesApiFactory(string containerConnectionString)
    {
        // SQL Server has no ":memory:" concept the way SQLite does, but
        // EF Core's Database.MigrateAsync() creates the target database
        // itself on first connect if it doesn't already exist -- so
        // simply pointing at a database name nobody has used yet is
        // enough to get a fresh, empty, isolated database per test.
        var connectionStringBuilder = new SqlConnectionStringBuilder(containerConnectionString)
        {
            InitialCatalog = $"quotes_test_{Guid.NewGuid():N}"
        };

        _connectionString = connectionStringBuilder.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Removing the DbContextOptions<QuotesDbContext> descriptor alone
            // is NOT enough here, unlike the SQLite factory: since EF Core 5,
            // AddDbContext<T>(...) also registers its configuration lambda as
            // an IDbContextOptionsConfiguration<T> entry, and those are
            // ADDITIVE, not replaced. Leaving the original one in place means
            // InfrastructureExtensions's UseSqlite(...) call still runs
            // alongside this UseSqlServer(...) call against the same
            // options object, and EF Core throws "Services for database
            // providers ... have been registered" because two providers
            // ended up configured at once. This is a real, first-time-seen
            // failure mode: the SQLite factory has the identical
            // SingleOrDefault-based removal and never hits this, purely
            // because swapping SQLite for SQLite doesn't trip EF's
            // single-provider check -- only a genuine cross-provider swap
            // like this one exposes it.
            services.RemoveAll<DbContextOptions<QuotesDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<QuotesDbContext>>();

            services.AddDbContext<QuotesDbContext>(options =>
                options.UseSqlServer(
                    _connectionString,
                    x =>
                    {
                        x.MigrationsAssembly("QuotesApi.Migrations.SqlServer");

                        // A real network hop to a real server, unlike
                        // SQLite's in-process connection -- brief,
                        // transient failures (a dropped connection, a
                        // moment of resource pressure on a busy CI
                        // runner) are a normal cost of testing against
                        // an actual database engine, not a sign
                        // something is broken. EnableRetryOnFailure
                        // wraps EF's own operations in a retry policy for
                        // exactly those transient SQL errors; it does not
                        // mask real failures like a bad connection string
                        // or a missing migration, which still fail
                        // immediately.
                        x.EnableRetryOnFailure(maxRetryCount: 3);
                    }));

            var clockDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IClock));
            if (clockDescriptor is not null)
                services.Remove(clockDescriptor);

            services.AddSingleton<IClock>(Clock);
        });
    }

    // Deliberately not dropping the per-test database here: DROP DATABASE
    // can fail with "database is currently in use" if pooled connections
    // haven't fully released yet, and the whole container (and every
    // database on it) is destroyed anyway when MsSqlContainerFixture
    // disposes at the end of the run. Simpler and more reliable to just
    // let per-test databases accumulate for the lifetime of one test run.
}
