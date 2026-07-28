using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId",
                table: "Documents");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedOutAt",
                table: "Documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckedOutByUserId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CheckedOutByUserId",
                table: "Documents",
                column: "CheckedOutByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_CheckedOutByUserId",
                table: "Documents",
                columns: new[] { "TenantId", "CheckedOutByUserId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documents_Checkout_Consistency",
                table: "Documents",
                sql: "(\"CheckedOutByUserId\" IS NULL AND \"CheckedOutAt\" IS NULL) OR (\"CheckedOutByUserId\" IS NOT NULL AND \"CheckedOutAt\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Users_CheckedOutByUserId",
                table: "Documents",
                column: "CheckedOutByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Users_CheckedOutByUserId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_CheckedOutByUserId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_CheckedOutByUserId",
                table: "Documents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documents_Checkout_Consistency",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "CheckedOutAt",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "CheckedOutByUserId",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId",
                table: "Documents",
                column: "TenantId");
        }
    }
}
