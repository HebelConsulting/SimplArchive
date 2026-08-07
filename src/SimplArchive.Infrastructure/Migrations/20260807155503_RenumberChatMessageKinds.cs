using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Removes the old DocumentFiled kind and closes the hole it leaves (ADR 0545). A first version used to emit
    /// a DocumentFiled entry (1) BESIDE its own VersionFiled one (2), so filing a document said the same thing
    /// twice — and the second sentence, "saved a new working version of this document", was false for a document
    /// that had no earlier version. VersionFiled now supplies both sentences, choosing by version number, so
    /// every kind-1 row duplicates the kind-2 row beside it (same document, same author, same moment).
    ///
    /// The survivors then shift down — 2 → 1, 3 → 2 — so the enum carries no retired value for a future reader to
    /// wonder about. The two UPDATEs cannot collide: the first only matches rows that are 2 BEFORE it runs, and
    /// the second only rows that are still 3, which the first never produced.
    ///
    /// The constraint is dropped FIRST and re-added last, because every intermediate state violates one or the
    /// other of them — a kind-1 row naming a version is illegal under the old constraint, a kind-3 row under the
    /// new one.
    ///
    /// Listed in MigrationDataPreservationTests' DestructiveAllowlist: it deletes rows, but no thread loses
    /// information — it stops each filing being announced twice.
    /// </remarks>
    public partial class RenumberChatMessageKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages");

            // Nothing offers replying to a system entry, but the data model permits it and ParentMessageId is
            // RESTRICT, so a stray reply would block the delete below. Promote any such reply to top-level rather
            // than deleting it: the entry it answered is going away, but a PERSON wrote the reply and its text is
            // not this migration's to discard.
            migrationBuilder.Sql(
                """
                UPDATE "ChatMessages" SET "ParentMessageId" = NULL
                WHERE "ParentMessageId" IN (SELECT "Id" FROM "ChatMessages" WHERE "Kind" = 1);
                """);

            // Mentions cascade, and a retired row carries none anyway — its Body is empty by construction.
            migrationBuilder.Sql("""DELETE FROM "ChatMessages" WHERE "Kind" = 1;""");

            migrationBuilder.Sql("""UPDATE "ChatMessages" SET "Kind" = 1 WHERE "Kind" = 2;""");
            migrationBuilder.Sql("""UPDATE "ChatMessages" SET "Kind" = 2 WHERE "Kind" = 3;""");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages",
                sql: "(\"Kind\" = 0 AND \"DocumentVersionId\" IS NULL) OR (\"Kind\" IN (1, 2) AND \"DocumentVersionId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Shifts the numbering back so the old code reads the same rows correctly. The deleted entries are NOT
        /// recreated: each said what the row beside it still says, so reviving them would reintroduce the
        /// duplication rather than recover anything.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages");

            // Highest first here — the reverse order of Up's — so a shifted row is never shifted twice.
            migrationBuilder.Sql("""UPDATE "ChatMessages" SET "Kind" = 3 WHERE "Kind" = 2;""");
            migrationBuilder.Sql("""UPDATE "ChatMessages" SET "Kind" = 2 WHERE "Kind" = 1;""");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages",
                sql: "(\"Kind\" IN (0, 1) AND \"DocumentVersionId\" IS NULL) OR (\"Kind\" IN (2, 3) AND \"DocumentVersionId\" IS NOT NULL)");
        }
    }
}
