using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShineMusic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLyricsTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 人工补充的中文翻译歌词（可选）：入库时作为非默认对照行写入 Lyrics 表。
            migrationBuilder.AddColumn<string>(
                name: "LyricsTranslation",
                table: "LibraryImportItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LyricsTranslation",
                table: "LibraryImportItems");
        }
    }
}
