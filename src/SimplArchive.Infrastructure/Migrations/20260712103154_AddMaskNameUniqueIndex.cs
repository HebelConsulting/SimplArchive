using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Masks_TenantId",
                table: "Masks");

            migrationBuilder.CreateIndex(
                name: "IX_Masks_TenantId_Name",
                table: "Masks",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Masks_TenantId_Name",
                table: "Masks");

            migrationBuilder.CreateIndex(
                name: "IX_Masks_TenantId",
                table: "Masks",
                column: "TenantId");
        }
    }
}
