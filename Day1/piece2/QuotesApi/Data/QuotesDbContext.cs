using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class QuotesDbContext : DbContext
{
    public QuotesDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Email)
                .IsRequired()
                .HasMaxLength(255);

            entity.HasIndex(x => x.Email)
                .IsUnique();

            entity.Property(x => x.PasswordHash)
                .IsRequired();

            entity.Property(x => x.CreatedAt)
                .IsRequired();
        });

        modelBuilder.Entity<Collection>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(80);

            entity.Property(x => x.OwnerId)
                .IsRequired();

            entity.OwnsMany(x => x.Items, item =>
            {
                item.WithOwner()
                    .HasForeignKey("CollectionId");

                item.Property<int>("CollectionId")
                    .ValueGeneratedNever();

                item.Property(x => x.QuoteId)
                    .IsRequired()
                    .ValueGeneratedNever();

                item.Property(x => x.AddedAt)
                    .IsRequired();

                item.HasKey("CollectionId", "QuoteId");
            });
        });
    }
}