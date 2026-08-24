using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteBackgroundImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackgroundImageUrl",
                table: "Quotes",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "/quote-backgrounds/mountain-1.jpg");

            migrationBuilder.Sql(
                """
                UPDATE Quotes
                SET BackgroundImageUrl = CASE (Id % 6)
                    WHEN 0 THEN '/quote-backgrounds/mountain-1.jpg'
                    WHEN 1 THEN '/quote-backgrounds/mountain-2.jpg'
                    WHEN 2 THEN '/quote-backgrounds/mountain-3.jpg'
                    WHEN 3 THEN '/quote-backgrounds/mountain-4.jpg'
                    WHEN 4 THEN '/quote-backgrounds/mountain-5.jpg'
                    ELSE '/quote-backgrounds/mountain-6.jpg'
                END
                WHERE BackgroundImageUrl IS NULL OR BackgroundImageUrl = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackgroundImageUrl",
                table: "Quotes");
        }
    }
}
