using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedSearchScopedSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add ShareScope, fold the old all-tenant IsShared bool into it (true → Everyone = 1; false stays the
            // default Private = 0), then drop IsShared — data-preserving (ADR "Scoped saved-search sharing";
            // allowlisted in MigrationDataPreservationTests). The backfill is Postgres-only; SQLite tests build
            // the current model directly via EnsureCreated and never run migrations.
            migrationBuilder.AddColumn<int>(
                name: "ShareScope",
                table: "SavedSearches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("UPDATE \"SavedSearches\" SET \"ShareScope\" = 1 WHERE \"IsShared\" = true;");
            }

            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "SavedSearches");

            migrationBuilder.CreateTable(
                name: "SavedSearchShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedSearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSearchShares", x => x.Id);
                    table.CheckConstraint("CK_SavedSearchShares_ExactlyOnePrincipal", "(CASE WHEN \"UserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"GroupId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.ForeignKey(
                        name: "FK_SavedSearchShares_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedSearchShares_SavedSearches_SavedSearchId",
                        column: x => x.SavedSearchId,
                        principalTable: "SavedSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedSearchShares_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedSearchShares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearchShares_GroupId",
                table: "SavedSearchShares",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearchShares_SavedSearchId",
                table: "SavedSearchShares",
                column: "SavedSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearchShares_TenantId_SavedSearchId_GroupId",
                table: "SavedSearchShares",
                columns: new[] { "TenantId", "SavedSearchId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearchShares_TenantId_SavedSearchId_UserId",
                table: "SavedSearchShares",
                columns: new[] { "TenantId", "SavedSearchId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearchShares_UserId",
                table: "SavedSearchShares",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedSearchShares");

            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "SavedSearches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Reverse the fold: Everyone (1) → shared; every other scope → private.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("UPDATE \"SavedSearches\" SET \"IsShared\" = true WHERE \"ShareScope\" = 1;");
            }

            migrationBuilder.DropColumn(
                name: "ShareScope",
                table: "SavedSearches");
        }
    }
}
