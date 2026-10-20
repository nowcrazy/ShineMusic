using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackItunesMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Explicit",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ItunesTrackId",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "Tracks",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Explicit",
                table: "LibraryImportItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ItunesCountry",
                table: "LibraryImportItems",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ItunesTrackId",
                table: "LibraryImportItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "LibraryImportItems",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Explicit",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ItunesTrackId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Explicit",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "ItunesCountry",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "ItunesTrackId",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "LibraryImportItems");
        }
    }
}
