using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <summary>
    /// Every document wears a mask (#1240): back-fill the untyped rows, then make the column required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The scaffolded version of this was replaced, and the reason is worth keeping.</b> EF generated an
    /// <c>AlterColumn</c> with <c>defaultValue: 00000000-0000-0000-0000-000000000000</c>, which would point
    /// every previously-untyped document at a mask version that does not exist — a row the foreign key must
    /// reject, and a silent corruption if it ever did not. A required column needs a real value per row, and
    /// only a query can say which.
    /// </para>
    /// <para>
    /// <b>The rule is the document's own shape:</b> Basic Entry where it has content, Folder where it has none.
    /// That keys the type off what the document IS rather than off whichever code path failed to give it one —
    /// and of the four paths that produced an untyped document, only two were a user choosing.
    /// </para>
    /// <para>
    /// <b>Data-preserving:</b> nothing is dropped and no row is deleted, so this needs no entry in
    /// <c>MigrationDataPreservationTests</c>'s destructive allowlist.
    /// </para>
    /// </remarks>
    public partial class DocumentMaskIsRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Content ⇒ Basic Entry. Correlated per TENANT: the well-known mask ids are shared by every tenant
            // (ADR 0198), but their CURRENT VERSIONS are not, so a single global lookup would file one tenant's
            // documents under another tenant's mask version.
            migrationBuilder.Sql(
                """
                UPDATE "Documents" d
                SET "MaskVersionId" = mv."Id"
                FROM "MaskVersions" mv
                WHERE d."MaskVersionId" IS NULL
                  AND mv."TenantId" = d."TenantId"
                  AND mv."MaskId" = 'e10e1000-e100-e100-e100-e10e10e10e30'
                  AND mv."IsCurrent"
                  AND EXISTS (SELECT 1 FROM "DocumentVersions" v WHERE v."DocumentId" = d."Id");
                """);

            // No content ⇒ Folder. A repository is a document with no parent (ADR 0200) and no versions, so it
            // lands here too, which is what it should wear.
            migrationBuilder.Sql(
                """
                UPDATE "Documents" d
                SET "MaskVersionId" = mv."Id"
                FROM "MaskVersions" mv
                WHERE d."MaskVersionId" IS NULL
                  AND mv."TenantId" = d."TenantId"
                  AND mv."MaskId" = 'e10e1000-e100-e100-e100-e10e10e10e31'
                  AND mv."IsCurrent";
                """);

            // REFUSE rather than invent. A row still untyped here belongs to a tenant with no current generic
            // mask seeded — which the seeder and the startup heal (ADR 0757) are supposed to make impossible.
            // Failing the migration says so; a default value would bury it under a foreign key that cannot hold.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE untyped bigint;
                BEGIN
                    SELECT count(*) INTO untyped FROM "Documents" WHERE "MaskVersionId" IS NULL;
                    IF untyped > 0 THEN
                        RAISE EXCEPTION
                            'Cannot make Documents.MaskVersionId required: % document(s) still have no mask, '
                            'because their tenant has no current Folder or Basic Entry mask version seeded. '
                            'Seed the well-known masks for those tenants and re-run this migration.', untyped;
                    END IF;
                END $$;
                """);

            // No defaultValue: the column is required, and every row now carries a real one. A DEFAULT here
            // would hand the next inserter the same unusable all-zero guid the scaffold proposed.
            migrationBuilder.Sql(
                """ALTER TABLE "Documents" ALTER COLUMN "MaskVersionId" SET NOT NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the constraint comes off. The back-filled masks STAY: they are real, correct types, and
            // reversing them would mean guessing which rows had been untyped before — information this
            // migration deliberately did not keep, because a document without a mask is not a state to restore.
            migrationBuilder.Sql(
                """ALTER TABLE "Documents" ALTER COLUMN "MaskVersionId" DROP NOT NULL;""");
        }
    }
}
