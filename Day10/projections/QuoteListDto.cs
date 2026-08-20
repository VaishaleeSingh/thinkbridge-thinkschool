namespace QueryTranslation.Demo;

/// <summary>
/// What a "list the quotes by this author" endpoint actually needs to return:
/// an id to link to, and the author's name to display. Notably absent: Text.
///
/// This is the type the projection in Part 2 targets. The reason it matters is
/// that EF Core builds its SELECT list from the projection, not from the entity
/// -- so the columns this DTO does NOT mention are columns the database is
/// never asked for and never sends.
/// </summary>
public class QuoteListDto
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
}

/// <summary>
/// A second DTO used only by Part 3b, to show a projection that *looks* narrow
/// but isn't -- because how Preview gets computed decides whether the Text
/// column crosses the wire or not. Same DTO, two ways to fill it, two very
/// different SQL statements.
/// </summary>
public class QuotePreviewDto
{
    public int Id { get; set; }
    public string Preview { get; set; } = "";
}
