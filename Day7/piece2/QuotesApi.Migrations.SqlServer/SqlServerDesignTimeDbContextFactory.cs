using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using QuotesApi.Data;

namespace QuotesApi.Migrations.SqlServer;

/// <summary>
/// This project holds a SEPARATE migration history from QuotesApi's own
/// Migrations/ folder, on purpose. Those existing migrations were
/// scaffolded against the Sqlite provider and bake in Sqlite-specific
/// column types ("TEXT", "INTEGER") plus a "Sqlite:Autoincrement"
/// annotation on every int primary key. SQL Server's migration generator
/// does not understand that annotation and simply ignores it, which means
/// replaying these exact migration files against SQL Server would create
/// every Id column as a plain int with NO identity/auto-increment
/// behavior -- and every insert relies on the database generating that
/// value. EF Core's supported answer for "one model, multiple providers"
/// is a distinct migrations assembly per provider, which is what this
/// project is.
///
/// `dotnet ef migrations add` needs this factory to build a QuotesDbContext
/// at design time, since there's no running Program.cs/DI container to ask
/// for one from this standalone project. The connection string below is
/// never actually opened for `migrations add` (that command only inspects
/// the model to diff against the last migration) -- a real, working
/// connection string comes from Testcontainers at test-run time, via
/// SqlServerQuotesApiFactory in Quotes.Tests.Integration.SqlServer.
///
/// To (re)generate the migration files this project needs, install the
/// dotnet-ef tool if you don't already have it:
///
///   dotnet tool install --global dotnet-ef
///
/// then, from Day3/piece2, run:
///
///   dotnet ef migrations add InitialCreate --project QuotesApi.Migrations.SqlServer --startup-project QuotesApi.Migrations.SqlServer --context QuotesApi.Data.QuotesDbContext --output-dir Migrations
///
/// This is a one-time step (plus again any time QuotesDbContext's model
/// itself changes) -- it does not need Docker or a live SQL Server
/// instance to run, only the SQL Server provider package, which this
/// project already references.
/// </summary>
public class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<QuotesDbContext>
{
    public QuotesDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<QuotesDbContext>();

        optionsBuilder.UseSqlServer(
            "Server=(local);Database=QuotesApi.DesignTime;Trusted_Connection=True;TrustServerCertificate=True;",
            x => x.MigrationsAssembly("QuotesApi.Migrations.SqlServer"));

        return new QuotesDbContext(optionsBuilder.Options);
    }
}
