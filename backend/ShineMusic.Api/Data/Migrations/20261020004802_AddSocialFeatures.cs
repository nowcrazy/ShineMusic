using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackComments_TrackComments_ParentId",
                        column: x => x.ParentId,
                        principalTable: "TrackComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackComments_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserFollows",
                columns: table => new
                {
                    FollowerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolloweeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFollows", x => new { x.FollowerId, x.FolloweeId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackComments_ParentId",
                table: "TrackComments",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackComments_TrackId_CreatedAtUtc",
                table: "TrackComments",
                columns: new[] { "TrackId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserFollows_FolloweeId_CreatedAtUtc",
                table: "UserFollows",
                columns: new[] { "FolloweeId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserFollows_FollowerId_CreatedAtUtc",
                table: "UserFollows",
                columns: new[] { "FollowerId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackComments");

            migrationBuilder.DropTable(
                name: "UserFollows");
        }
    }
}
