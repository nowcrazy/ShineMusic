using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsSourceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "Lyrics",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "Lyrics",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Lyrics",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<double>(
                name: "LyricsConfidence",
                table: "LibraryImportItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LyricsProviderId",
                table: "LibraryImportItems",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LyricsSource",
                table: "LibraryImportItems",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "Lyrics");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "Lyrics");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Lyrics");

            migrationBuilder.DropColumn(
                name: "LyricsConfidence",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "LyricsProviderId",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "LyricsSource",
                table: "LibraryImportItems");
        }
    }
}
