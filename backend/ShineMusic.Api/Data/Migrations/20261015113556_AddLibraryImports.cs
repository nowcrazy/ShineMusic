using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "MediaAssets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE MediaAssets SET ContentHash = 'legacy:' || Id WHERE ContentHash = '';");

            migrationBuilder.CreateTable(
                name: "LibraryImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryImportItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SourceStorageKey = table.Column<string>(type: "TEXT", nullable: false),
                    TranscodedStorageKey = table.Column<string>(type: "TEXT", nullable: true),
                    ArtworkStorageKey = table.Column<string>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Container = table.Column<string>(type: "TEXT", nullable: true),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    BitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumTitle = table.Column<string>(type: "TEXT", nullable: true),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    DiscNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseYear = table.Column<int>(type: "INTEGER", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    TranscodeStatus = table.Column<string>(type: "TEXT", nullable: true),
                    TranscodeFormat = table.Column<string>(type: "TEXT", nullable: true),
                    TranscodeBitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    SongRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MetadataReviewed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryImportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryImportItems_LibraryImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "LibraryImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_ContentHash",
                table: "MediaAssets",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImportBatches_CreatedByUserId_CreatedAtUtc",
                table: "LibraryImportBatches",
                columns: new[] { "CreatedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImportItems_BatchId",
                table: "LibraryImportItems",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImportItems_ContentHash",
                table: "LibraryImportItems",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImportItems_Status_CreatedAtUtc",
                table: "LibraryImportItems",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryImportItems");

            migrationBuilder.DropTable(
                name: "LibraryImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_ContentHash",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "MediaAssets");
        }
    }
}
