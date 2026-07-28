using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrLanguageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultOcrLanguages",
                table: "Tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "eng+deu+fra+ita");

            migrationBuilder.AddColumn<string>(
                name: "OcrLanguages",
                table: "DocumentVersions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultOcrLanguages",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OcrLanguages",
                table: "DocumentVersions");
        }
    }
}
