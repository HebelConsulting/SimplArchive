using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillFolderMask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill: give every existing folder (a Document with no versions and no mask) its tenant's current
            // "Folder" mask version, so the "all folders are the Folder mask" invariant holds retroactively (ADR
            // "Folder mask on folders"). Leaves (documents with a version) are left alone. Postgres UPDATE…FROM.
            migrationBuilder.Sql(
                """
                UPDATE "Documents" AS d
                SET "MaskVersionId" = mv."Id"
                FROM "MaskVersions" AS mv
                WHERE mv."TenantId" = d."TenantId"
                  AND mv."MaskId" = 'E10E1000-E100-E100-E100-E10E10E10E31'
                  AND mv."IsCurrent" = TRUE
                  AND d."MaskVersionId" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM "DocumentVersions" v WHERE v."DocumentId" = d."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
