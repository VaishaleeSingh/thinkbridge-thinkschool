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

    // Day 19 -- Service Bus messaging tables
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    // Day 20 -- the transactional outbox. Written in the same transaction as
    // the domain change it describes; drained by OutboxRelayService.
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<QuoteAuditEntry> QuoteAuditEntries => Set<QuoteAuditEntry>();
    public DbSet<QuoteSearchProjection> QuoteSearchProjections => Set<QuoteSearchProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Day 11 -- the index the profiling exercise proved was missing.
        //
        // Until now Quote was the ONLY entity in this method with no
        // configuration at all, which is exactly why dbo.Quotes had no index on
        // Author: nothing ever asked for one. Day 11's profile made the cost
        // concrete -- a per-author COUNT had to scan the whole table, and an
        // N+1 turned one scan into 500 of them per request.
        //
        // This lives here rather than in the runtime toggle endpoint on
        // purpose. The toggle was a measuring instrument: it let the same
        // endpoint be profiled with and without the index in one sitting. It is
        // the wrong home for the fix, because an index that only exists when
        // someone POSTs to a diagnostics route is an index that will be absent
        // in every environment that matters. Declaring it on the model means it
        // is created by migration, reproducible, and cannot silently go missing.
        //
        // Deliberately NOT covering (no .IncludeProperties(x => x.Text)): the
        // queries this serves filter and group by Author and never read Text
        // through this path, so including a ~600-character column would make
        // every leaf page carry weight nothing asks for -- the mistake Day 8's
        // covering-index task flagged.
        modelBuilder.Entity<Quote>(entity =>
        {
            entity.Property(x => x.BackgroundImageUrl)
                .IsRequired()
                .HasMaxLength(500);

            entity.HasIndex(x => x.Author);
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

        // Day 19 -- ProcessedMessages: composite PK is the idempotency
        // guarantee under competing consumers. Index on ProcessedAtUtc
        // supports the cleanup/retention query.
        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.HasKey(x => new { x.MessageId, x.SubscriptionName });

            entity.Property(x => x.MessageId)
                .IsRequired()
                .HasMaxLength(128);

            entity.Property(x => x.SubscriptionName)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(x => x.Outcome)
                .IsRequired()
                .HasMaxLength(50);

            entity.HasIndex(x => x.ProcessedAtUtc);
        });

        modelBuilder.Entity<QuoteAuditEntry>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.EventId)
                .IsRequired()
                .HasMaxLength(128);

            entity.Property(x => x.EventType)
                .IsRequired()
                .HasMaxLength(50);

            entity.HasIndex(x => x.QuoteId);
            entity.HasIndex(x => x.RecordedAtUtc);
        });

        // Day 20 -- OutboxMessages.
        //
        // Three constraints here are load-bearing, and none of them is
        // convention:
        //
        //   1. MessageId is UNIQUE. It becomes the broker's MessageId, and it
        //      is a deterministic hash of the event, so two rows carrying it
        //      would be the same logical event enqueued twice. The database
        //      says that cannot happen rather than a code path assuming it.
        //
        //   2. The (Status, Id) index is FILTERED to pending rows. The claim
        //      query runs on every tick forever; unfiltered, this index would
        //      grow with the full history of every event ever published and
        //      the claim would get slower every day the app stays up. Filtered,
        //      it is proportional to the backlog, which is normally near zero.
        //
        //   3. Payload has no length cap. A capped column that silently
        //      truncates an event body would produce a message that
        //      deserialises to garbage on the consumer -- a poison message
        //      manufactured by the producer's own schema.
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).ValueGeneratedOnAdd();

            entity.Property(x => x.MessageId)
                .IsRequired()
                .HasMaxLength(128);

            entity.HasIndex(x => x.MessageId).IsUnique();

            entity.Property(x => x.EventType)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(x => x.SchemaVersion)
                .IsRequired()
                .HasMaxLength(16);

            entity.Property(x => x.Payload)
                .IsRequired();

            entity.Property(x => x.TraceParent)
                .HasMaxLength(64);

            entity.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(16);

            entity.Property(x => x.LockOwner)
                .HasMaxLength(64);

            entity.Property(x => x.LastError)
                .HasMaxLength(512);

            // The claim path. HasFilter is provider-specific SQL, and both
            // providers accept this predicate as written.
            entity.HasIndex(x => new { x.Status, x.Id })
                .HasFilter("[Status] = 'Pending'")
                .HasDatabaseName("IX_OutboxMessages_Pending");

            // The retention sweep.
            entity.HasIndex(x => x.SentAtUtc);
        });

        modelBuilder.Entity<QuoteSearchProjection>(entity =>
        {
            entity.HasKey(x => x.QuoteId);

            // NOT database-generated. QuoteId arrives in the event; the
            // projection is keyed by the quote it describes. Left to EF's
            // convention an int key becomes IDENTITY on SQL Server, and the
            // first upsert fails with "Cannot insert explicit value for
            // identity column" -- on SQL Server only, so SQLite locally would
            // have hidden it until deployment.
            entity.Property(x => x.QuoteId).ValueGeneratedNever();

            entity.Property(x => x.Author)
                .HasMaxLength(200);

            entity.Property(x => x.Text)
                .HasMaxLength(1000);
        });
    }
}
