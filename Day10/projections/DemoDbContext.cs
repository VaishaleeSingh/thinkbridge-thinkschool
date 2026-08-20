using Microsoft.EntityFrameworkCore;

namespace QueryTranslation.Demo;

public class DemoDbContext : DbContext
{
    public DemoDbContext(DbContextOptions<DemoDbContext> options) : base(options)
    {
    }

    public DbSet<Quote> Quotes => Set<Quote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Quote>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Author).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Text).IsRequired().HasMaxLength(1000);
            entity.Property(x => x.CreatedAt).IsRequired();

            // Part 3a's "correct" query filters on Author, so give it an index --
            // otherwise the comparison there would be muddied by a table scan on
            // both sides, and the point being made is about WHERE clauses
            // reaching the database at all, not about index choice.
            entity.HasIndex(x => x.Author);
        });
    }
}
