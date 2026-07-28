using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldDefinitionNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FieldDefinitions_MaskId",
                table: "FieldDefinitions");

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitions_MaskId_Name",
                table: "FieldDefinitions",
                columns: new[] { "MaskId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FieldDefinitions_MaskId_Name",
                table: "FieldDefinitions");

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitions_MaskId",
                table: "FieldDefinitions",
                column: "MaskId");
        }
    }
}
