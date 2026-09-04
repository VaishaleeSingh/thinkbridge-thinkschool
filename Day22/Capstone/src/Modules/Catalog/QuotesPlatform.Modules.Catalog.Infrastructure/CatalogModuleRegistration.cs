using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace QuotesPlatform.Modules.Catalog.Infrastructure;

/// <summary>
/// The module's own composition, so the Host wires modules rather than types.
///
/// This is the seam that keeps the Host from becoming the place where every
/// module's internals are known: Program.cs calls AddCatalogModule and cannot
/// see a repository, a DbContext or a handler. A module that needs a new
/// service registers it here, and nothing outside changes.
/// </summary>
public static class CatalogModuleRegistration
{
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<CatalogDbContext>(options => options.UseSqlite(connectionString));

        // Repositories and use-case handlers are registered here as they are
        // written. Day 22 is the scaffold: the boundary is what is being
        // established today, not the feature set.

        return services;
    }
}
