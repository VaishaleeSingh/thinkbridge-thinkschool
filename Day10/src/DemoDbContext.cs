using Microsoft.EntityFrameworkCore;

namespace EfCoreChangeTracker.Demo;

public class DemoDbContext : DbContext
{
    public DemoDbContext(DbContextOptions<DemoDbContext> options) : base(options)
    {
    }

    public DbSet<Quote> Quotes => Set<Quote>();
}
