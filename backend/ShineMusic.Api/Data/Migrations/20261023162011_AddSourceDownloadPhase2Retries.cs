using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceDownloadPhase2Retries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProcessingRetryCount",
                table: "LibraryImportItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AutoRetryCount",
                table: "ExternalDownloadTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAtUtc",
                table: "ExternalDownloadTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingRetryCount",
                table: "LibraryImportItems");

            migrationBuilder.DropColumn(
                name: "AutoRetryCount",
                table: "ExternalDownloadTasks");

            migrationBuilder.DropColumn(
                name: "NextRetryAtUtc",
                table: "ExternalDownloadTasks");
        }
    }
}
