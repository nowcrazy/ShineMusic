using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddListeningEventsAndRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyRecommendationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DateKey = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyRecommendationItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListeningEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PositionMs = table.Column<long>(type: "INTEGER", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListeningEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTrackFeedbacks",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTrackFeedbacks", x => new { x.UserId, x.TrackId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyRecommendationItems_UserId_DateKey_Rank",
                table: "DailyRecommendationItems",
                columns: new[] { "UserId", "DateKey", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyRecommendationItems_UserId_DateKey_TrackId",
                table: "DailyRecommendationItems",
                columns: new[] { "UserId", "DateKey", "TrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListeningEvents_UserId_OccurredAtUtc",
                table: "ListeningEvents",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ListeningEvents_UserId_TrackId",
                table: "ListeningEvents",
                columns: new[] { "UserId", "TrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserTrackFeedbacks_UserId_Kind",
                table: "UserTrackFeedbacks",
                columns: new[] { "UserId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyRecommendationItems");

            migrationBuilder.DropTable(
                name: "ListeningEvents");

            migrationBuilder.DropTable(
                name: "UserTrackFeedbacks");
        }
    }
}
