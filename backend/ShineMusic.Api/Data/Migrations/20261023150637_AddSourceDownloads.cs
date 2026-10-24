using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoScore",
                table: "LibraryImportItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoScoreBreakdownJson",
                table: "LibraryImportItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceProvider",
                table: "LibraryImportItems",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalDownloadTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderTrackId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ArtistsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Quality = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    License = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourcePageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CoverUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AutoScore = table.Column<int>(type: "INTEGER", nullable: true),
                    AutoPublished = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ImportItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalDownloadTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDownloadTasks_Provider_ProviderTrackId",
                table: "ExternalDownloadTasks",
                columns: new[] { "Provider", "ProviderTrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDownloadTasks_Status_CreatedAtUtc",
                table: "ExternalDownloadTasks",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalDownloadTasks");

            migrationBuilder.DropColumn(
                name: "AutoScore",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "AutoScoreBreakdownJson",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "SourceProvider",
                table: "LibraryImportItems");
        }
    }
}
