using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentServiceAccountAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "Documents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByServiceAccountId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CreatedByServiceAccountId",
                table: "Documents",
                column: "CreatedByServiceAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documents_ExactlyOneCreator",
                table: "Documents",
                sql: "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_ServiceAccounts_CreatedByServiceAccountId",
                table: "Documents",
                column: "CreatedByServiceAccountId",
                principalTable: "ServiceAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_ServiceAccounts_CreatedByServiceAccountId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_CreatedByServiceAccountId",
                table: "Documents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documents_ExactlyOneCreator",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "CreatedByServiceAccountId",
                table: "Documents");

            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "Documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
