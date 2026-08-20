namespace EfCoreChangeTracker.Demo;

// Deliberately the same shape as QuotesApi.Models.Quote (Id/Author/Text)
// so this demo's findings transfer directly to the real DbContext -- but
// kept as its own tiny model in its own tiny DbContext (below) rather than
// referencing QuotesApi's project directly. This is a change-tracker demo,
// not a QuotesApi feature; it shouldn't need QuotesApi's auth, migrations,
// or SQL Server dependency just to prove a point about EF Core's tracker
// that applies to any entity type.
public class Quote
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
}
