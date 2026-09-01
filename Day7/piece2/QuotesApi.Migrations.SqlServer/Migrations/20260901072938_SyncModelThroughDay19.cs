using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelThroughDay19 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Author",
                table: "Quotes",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<string>(
                name: "BackgroundImageUrl",
                table: "Quotes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubscriptionName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => new { x.MessageId, x.SubscriptionName });
                });

            migrationBuilder.CreateTable(
                name: "QuoteAuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    QuoteId = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteSearchProjections",
                columns: table => new
                {
                    QuoteId = table.Column<int>(type: "int", nullable: false),
                    Author = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteSearchProjections", x => x.QuoteId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Author",
                table: "Quotes",
                column: "Author");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedMessages_ProcessedAtUtc",
                table: "ProcessedMessages",
                column: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteAuditEntries_QuoteId",
                table: "QuoteAuditEntries",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteAuditEntries_RecordedAtUtc",
                table: "QuoteAuditEntries",
                column: "RecordedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedMessages");

            migrationBuilder.DropTable(
                name: "QuoteAuditEntries");

            migrationBuilder.DropTable(
                name: "QuoteSearchProjections");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_Author",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "BackgroundImageUrl",
                table: "Quotes");

            migrationBuilder.AlterColumn<string>(
                name: "Author",
                table: "Quotes",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
