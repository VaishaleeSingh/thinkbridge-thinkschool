using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuotesApi.Data;

#nullable disable

namespace QuotesApi.Migrations
{
    /// <summary>
    /// Day 11 — the index Day 11's profiling proved was missing.
    ///
    /// Written by hand rather than generated with `dotnet ef migrations add`,
    /// because the sandbox this was authored in has no route to NuGet and so
    /// cannot run the EF tooling. A single-column index is one of the few
    /// migrations where hand-writing is genuinely safe: no data movement, no
    /// column type to infer, and the matching snapshot entry
    /// (`b.HasIndex("Author")` on QuotesApi.Models.Quote) is one line. The
    /// model, the snapshot, and this file were all updated together — miss any
    /// one and EF refuses to start with "the model has pending changes".
    ///
    /// The two attributes below matter more than they look. A generated
    /// migration puts them in a companion `.Designer.cs`; EF reads the
    /// [Migration] attribute to learn the migration's ID, and without it this
    /// file is not recognised as a migration at all — it compiles, the app
    /// starts, and the index is silently never created. That failure mode is
    /// invisible, which is exactly why it is worth stating.
    ///
    /// To swap in the generated version instead: delete this file, remove the
    /// snapshot line, and run `dotnet ef migrations add AddQuoteAuthorIndex`.
    /// </summary>
    [DbContext(typeof(QuotesDbContext))]
    [Migration("20260821120000_AddQuoteAuthorIndex")]
    public partial class AddQuoteAuthorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Not unique: many quotes share an author — that is the whole point
            // of indexing this column. Not covering either; see the comment on
            // the HasIndex call in QuotesDbContext for why Text is left out.
            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Author",
                table: "Quotes",
                column: "Author");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Quotes_Author",
                table: "Quotes");
        }
    }
}
