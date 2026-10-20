using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileTrackImportMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                name: "LyricsTranslation",
                table: "LibraryImportItems");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedEmail",
                table: "Invitations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Invitations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<string>(
                name: "LyricsTranslation",
                table: "LibraryImportItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedEmail",
                table: "Invitations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Invitations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
