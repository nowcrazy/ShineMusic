using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserThemeProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserThemeProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WallpaperExtension = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    Accent2 = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    Accent3 = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    MaskOpacity = table.Column<float>(type: "REAL", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserThemeProfiles", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserThemeProfiles");
        }
    }
}
