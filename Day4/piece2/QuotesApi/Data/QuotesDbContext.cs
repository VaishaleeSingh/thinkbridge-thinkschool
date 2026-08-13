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

    // Added for Day 3's own login/refresh-token system (separate from
    // Entra ID users, who are authenticated by Microsoft and never get a
    // row in either of these tables).
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

        // One row per person who can log in with email + password.
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Email)
                .IsRequired()
                .HasMaxLength(255);

            // Two accounts can't share an email -- this also lets
            // AuthService.LoginAsync safely assume
            // FirstOrDefaultAsync(u => u.Email == email) matches at most
            // one row.
            entity.HasIndex(x => x.Email)
                .IsUnique();

            entity.Property(x => x.PasswordHash)
                .IsRequired();

            entity.Property(x => x.CreatedAt)
                .IsRequired();
        });

        // One row per refresh token ever issued. Old, replaced tokens are
        // kept rather than deleted -- RevokedAt/ReplacedByToken record
        // their history instead, which is exactly what makes reuse
        // detection possible later.
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.TokenHash)
                .IsRequired();

            entity.Property(x => x.UserId)
                .IsRequired();

            entity.Property(x => x.ExpiresAt)
                .IsRequired();

            entity.Property(x => x.CreatedAt)
                .IsRequired();

            entity.Property(x => x.FamilyId)
                .HasMaxLength(100);

            // A token's hash should never collide with another token's --
            // this also keeps the WHERE TokenHash == ... lookup in
            // ValidateTokenAsync unambiguous.
            entity.HasIndex(x => x.TokenHash)
                .IsUnique();

            entity.HasIndex(x => x.UserId);

            // Looked up whenever a whole family needs to be revoked at
            // once (see RefreshTokenService.DetectAndRevokeReuseAsync).
            entity.HasIndex(x => x.FamilyId);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
