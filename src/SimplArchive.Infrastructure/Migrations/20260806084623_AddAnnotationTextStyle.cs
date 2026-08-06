using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnotationTextStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TextStyle_Bold",
                table: "DocumentAnnotations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextStyle_FontFamily",
                table: "DocumentAnnotations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TextStyle_FontSizePx",
                table: "DocumentAnnotations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TextStyle_Italic",
                table: "DocumentAnnotations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TextStyle_SizeBasis",
                table: "DocumentAnnotations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TextStyle_Strikethrough",
                table: "DocumentAnnotations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TextStyle_Underline",
                table: "DocumentAnnotations",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextStyle_Bold",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "TextStyle_FontFamily",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "TextStyle_FontSizePx",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "TextStyle_Italic",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "TextStyle_SizeBasis",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "TextStyle_Strikethrough",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "TextStyle_Underline",
                table: "DocumentAnnotations");
        }
    }
}
