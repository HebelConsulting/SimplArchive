using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalRepository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PersonalOfUserId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_PersonalOfUserId",
                table: "Documents",
                column: "PersonalOfUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_PersonalOfUserId",
                table: "Documents",
                columns: new[] { "TenantId", "PersonalOfUserId" },
                unique: true,
                filter: "\"PersonalOfUserId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Users_PersonalOfUserId",
                table: "Documents",
                column: "PersonalOfUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Users_PersonalOfUserId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_PersonalOfUserId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_PersonalOfUserId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "PersonalOfUserId",
                table: "Documents");
        }
    }
}
