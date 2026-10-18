using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserUploadsAndLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsUserUpload",
                table: "LibraryImportItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedTrackId",
                table: "LibraryImportItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserLibraryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLibraryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserLibraryItems_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserLibraryItems_TrackId",
                table: "UserLibraryItems",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLibraryItems_UserId_AddedAtUtc",
                table: "UserLibraryItems",
                columns: new[] { "UserId", "AddedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserLibraryItems_UserId_TrackId",
                table: "UserLibraryItems",
                columns: new[] { "UserId", "TrackId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserLibraryItems");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "IsUserUpload",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "PublishedTrackId",
                table: "LibraryImportItems");
        }
    }
}
