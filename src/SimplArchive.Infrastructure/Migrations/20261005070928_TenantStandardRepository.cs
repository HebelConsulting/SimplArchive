using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantStandardRepository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StandardRepositoryId",
                table: "Tenants",
                type: "uuid",
                nullable: true);

            // Backfill ONCE (ADR 0892): an existing tenant's standard repository is its OLDEST top-level document
            // that is not a personal space and not in the recycle bin — the one provisioning created — with ties
            // broken by Id. The tie-break is not decoration: provisioning stamps the administrator's personal
            // space and the repository with the SAME CreatedAt, and only the PersonalOfUserId test tells them
            // apart. A correlated subquery with ORDER BY/LIMIT, so the statement is the same on Postgres and SQLite.
            migrationBuilder.Sql(
                """
                UPDATE "Tenants"
                SET "StandardRepositoryId" = (
                    SELECT d."Id" FROM "Documents" AS d
                    WHERE d."TenantId" = "Tenants"."Id"
                      AND d."ParentId" IS NULL
                      AND d."PersonalOfUserId" IS NULL
                      AND d."DeletedAt" IS NULL
                    ORDER BY d."CreatedAt", d."Id"
                    LIMIT 1)
                WHERE "StandardRepositoryId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StandardRepositoryId",
                table: "Tenants");
        }
    }
}
