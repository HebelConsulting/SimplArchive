using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAclEntryServiceAccountPrincipal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_ExactlyOnePrincipal",
                table: "AclEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "ServiceAccountId",
                table: "AclEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_ServiceAccountId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "ServiceAccountId" },
                unique: true,
                filter: "\"ServiceAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_ServiceAccountId",
                table: "AclEntries",
                column: "ServiceAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_ExactlyOnePrincipal",
                table: "AclEntries",
                sql: "(CASE WHEN \"UserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"GroupId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"ServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_AclEntries_ServiceAccounts_ServiceAccountId",
                table: "AclEntries",
                column: "ServiceAccountId",
                principalTable: "ServiceAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AclEntries_ServiceAccounts_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_ExactlyOnePrincipal",
                table: "AclEntries");

            migrationBuilder.DropColumn(
                name: "ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_ExactlyOnePrincipal",
                table: "AclEntries",
                sql: "(\"UserId\" IS NOT NULL AND \"GroupId\" IS NULL) OR (\"UserId\" IS NULL AND \"GroupId\" IS NOT NULL)");
        }
    }
}
