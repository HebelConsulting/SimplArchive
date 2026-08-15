using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only (ADR 0590). No tables, columns, constraints or indexes change — three UPDATE/INSERT statements
    /// that finish what ADR 0369 started: "every folder wears the Folder mask".
    ///
    /// Why a second backfill is needed at all: 0369's ran once, and every tenant provisioned AFTERWARDS got a
    /// maskless repository root anyway, because provisioning resolves the mask through the ambient tenant and a
    /// PlatformAdministrator has none (ADR 0582). Personal repositories had the same defect (ADR 0590), and
    /// folders arriving from an external-system import were written maskless by the migration tooling.
    ///
    /// Leaves are never touched: the predicate requires a document with NO versions, so a real document that has
    /// not been classified keeps its null mask and its normal classification path.
    /// </summary>
    /// <inheritdoc />
    public partial class BackfillFolderMaskOnRemainingFolders : Migration
    {
        // The well-known ids, spelled out because a migration must not depend on application constants that can
        // be renamed later — the SQL has to keep meaning the same thing years from now.
        private const string FolderMaskId = "E10E1000-E100-E100-E100-E10E10E10E31";
        private const string UserFolderMaskId = "E10E1000-E100-E100-E100-E10E10E10E35";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Seed the personal-space mask for every tenant that predates it, so step 2 has something to point
            //    at. Mirrors WellKnownMaskSeeder: the mask, its current version, and its optional profile fields.
            migrationBuilder.Sql(
                $"""
                INSERT INTO "Masks" ("Id", "TenantId", "CreatedAt")
                SELECT '{UserFolderMaskId}', t."Id", NOW()
                FROM "Tenants" AS t
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Masks" AS m WHERE m."TenantId" = t."Id" AND m."Id" = '{UserFolderMaskId}');

                INSERT INTO "MaskVersions" ("Id", "TenantId", "MaskId", "Name", "VersionNumber", "IsCurrent", "CreatedAt")
                SELECT gen_random_uuid(), m."TenantId", m."Id", 'User Folder', 1, TRUE, NOW()
                FROM "Masks" AS m
                WHERE m."Id" = '{UserFolderMaskId}'
                  AND NOT EXISTS (
                      SELECT 1 FROM "MaskVersions" AS mv
                      WHERE mv."TenantId" = m."TenantId" AND mv."MaskId" = m."Id");

                INSERT INTO "FieldDefinitions" ("Id", "TenantId", "MaskVersionId", "Name", "DataType", "IsRequired", "CreatedAt")
                SELECT gen_random_uuid(), mv."TenantId", mv."Id", f."Name", 0, FALSE, NOW()
                FROM "MaskVersions" AS mv
                CROSS JOIN (VALUES ('Full name'), ('Title'), ('Degree'), ('Position'), ('Department'), ('Company'), ('Office'), ('Location'), ('Abbreviation'), ('Telephone'), ('Mobile'), ('Fax'), ('Email')) AS f("Name")
                WHERE mv."MaskId" = '{UserFolderMaskId}'
                  AND NOT EXISTS (
                      SELECT 1 FROM "FieldDefinitions" AS fd
                      WHERE fd."MaskVersionId" = mv."Id" AND fd."Name" = f."Name");
                """);

            // 2. Every PERSONAL repository wears the personal-space mask.
            migrationBuilder.Sql(
                $"""
                UPDATE "Documents" AS d
                SET "MaskVersionId" = mv."Id"
                FROM "MaskVersions" AS mv
                WHERE mv."TenantId" = d."TenantId"
                  AND mv."MaskId" = '{UserFolderMaskId}'
                  AND mv."IsCurrent" = TRUE
                  AND d."PersonalOfUserId" IS NOT NULL
                  AND d."MaskVersionId" IS NULL;
                """);

            // 3. Every remaining maskless FOLDER — repository roots the ambient-tenant defect left bare, and
            //    folders written by an import — wears the Folder mask. Same predicate as ADR 0369's backfill.
            migrationBuilder.Sql(
                $"""
                UPDATE "Documents" AS d
                SET "MaskVersionId" = mv."Id"
                FROM "MaskVersions" AS mv
                WHERE mv."TenantId" = d."TenantId"
                  AND mv."MaskId" = '{FolderMaskId}'
                  AND mv."IsCurrent" = TRUE
                  AND d."MaskVersionId" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM "DocumentVersions" v WHERE v."DocumentId" = d."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: this assigns a mask where one was missing, and "restoring" the absence would
            // re-break the invariant ADR 0369 established. A down-migration that destroys data to recreate a bug
            // is worse than no down-migration.
        }
    }
}
