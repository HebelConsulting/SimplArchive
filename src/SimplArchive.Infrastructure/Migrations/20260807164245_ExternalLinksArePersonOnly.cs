using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Only a PERSON shares a document (ADR 0546). A service account could hold CanCreateExternalLink and appear
    /// as a link's creator; both are removed here, and CreatedByUserId becomes required — so "a person did this"
    /// is enforced by the schema rather than by the code remembering to.
    ///
    /// Any link a service account created is DELETED first. It cannot be re-attributed (there is no person to
    /// attribute it to), and the alternative EF scaffolded — defaulting the creator to an all-zeros GUID — would
    /// either violate the FK to Users or, worse, leave a live external link attributed to nobody. A share whose
    /// creator cannot be named is exactly what this ADR set out to prevent, so it does not survive the migration.
    /// The revocation-is-a-stamp principle does not apply: that preserves the record of what a PERSON shared.
    ///
    /// Listed in MigrationDataPreservationTests' DestructiveAllowlist for both reasons — the dropped columns and
    /// those deleted rows.
    /// </remarks>
    public partial class ExternalLinksArePersonOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "ExternalLinks" WHERE "CreatedByServiceAccountId" IS NOT NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_ExternalLinks_ServiceAccounts_CreatedByServiceAccountId",
                table: "ExternalLinks");

            migrationBuilder.DropIndex(
                name: "IX_ExternalLinks_CreatedByServiceAccountId",
                table: "ExternalLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalLinks_ExactlyOneCreator",
                table: "ExternalLinks");

            migrationBuilder.DropColumn(
                name: "CanCreateExternalLink",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "CreatedByServiceAccountId",
                table: "ExternalLinks");

            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "ExternalLinks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Restores the columns and constraints. The deleted links are not recreated — a token nobody holds any
        /// more would be restored as live, and their creator was removed from the system by this same migration.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanCreateExternalLink",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "ExternalLinks",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByServiceAccountId",
                table: "ExternalLinks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_CreatedByServiceAccountId",
                table: "ExternalLinks",
                column: "CreatedByServiceAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalLinks_ExactlyOneCreator",
                table: "ExternalLinks",
                sql: "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ExternalLinks_ServiceAccounts_CreatedByServiceAccountId",
                table: "ExternalLinks",
                column: "CreatedByServiceAccountId",
                principalTable: "ServiceAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
