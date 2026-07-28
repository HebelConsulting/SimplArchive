using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusDeactivatedAtConsistencyChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Status_DeactivatedAt",
                table: "Tenants",
                sql: "(\"Status\" = 0 AND \"DeactivatedAt\" IS NULL) OR (\"Status\" = 1 AND \"DeactivatedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Repositories_Status_DeactivatedAt",
                table: "Repositories",
                sql: "(\"Status\" = 0 AND \"DeactivatedAt\" IS NULL) OR (\"Status\" = 1 AND \"DeactivatedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Status_DeactivatedAt",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Repositories_Status_DeactivatedAt",
                table: "Repositories");
        }
    }
}
