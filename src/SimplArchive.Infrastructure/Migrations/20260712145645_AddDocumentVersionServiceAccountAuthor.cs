using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVersionServiceAccountAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "DocumentVersions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByServiceAccountId",
                table: "DocumentVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_CreatedByServiceAccountId",
                table: "DocumentVersions",
                column: "CreatedByServiceAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentVersions_ExactlyOneCreator",
                table: "DocumentVersions",
                sql: "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentVersions_ServiceAccounts_CreatedByServiceAccountId",
                table: "DocumentVersions",
                column: "CreatedByServiceAccountId",
                principalTable: "ServiceAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DocumentVersions_ServiceAccounts_CreatedByServiceAccountId",
                table: "DocumentVersions");

            migrationBuilder.DropIndex(
                name: "IX_DocumentVersions_CreatedByServiceAccountId",
                table: "DocumentVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentVersions_ExactlyOneCreator",
                table: "DocumentVersions");

            migrationBuilder.DropColumn(
                name: "CreatedByServiceAccountId",
                table: "DocumentVersions");

            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "DocumentVersions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
