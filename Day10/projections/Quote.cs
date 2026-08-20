namespace QueryTranslation.Demo;

/// <summary>
/// Deliberately the same shape as QuotesApi.Models.Quote (Id / Author / Text /
/// CreatedByUserId) plus a CreatedAt, so what this demo proves about query
/// translation transfers directly to the real QuotesDbContext. Kept as its own
/// small model in its own small DbContext rather than referencing QuotesApi:
/// query translation and projection are EF Core core behaviors, and this
/// shouldn't need QuotesApi's auth, migrations, or SQL Server dependency just
/// to show what SQL EF generates.
///
/// The point of Text being long here is not decoration -- it's the whole
/// reason projection matters. A list endpoint that only needs Id and Author
/// has no reason to drag ~600 characters per row across the wire, and this
/// model makes that cost real instead of theoretical.
/// </summary>
public class Quote
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}
