using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Repositories_TenantId",
                table: "Repositories");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_TenantId_Name",
                table: "Repositories",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Repositories_TenantId_Name",
                table: "Repositories");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_TenantId",
                table: "Repositories",
                column: "TenantId");
        }
    }
}
