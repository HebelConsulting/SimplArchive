using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAclInheritance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_GroupId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_UserId",
                table: "AclEntries");

            migrationBuilder.AddColumn<bool>(
                name: "BreaksInheritance",
                table: "Documents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "RepositoryId",
                table: "AclEntries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentId",
                table: "AclEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_DocumentId_GroupId",
                table: "AclEntries",
                columns: new[] { "DocumentId", "GroupId" },
                unique: true,
                filter: "\"DocumentId\" IS NOT NULL AND \"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_DocumentId_ServiceAccountId",
                table: "AclEntries",
                columns: new[] { "DocumentId", "ServiceAccountId" },
                unique: true,
                filter: "\"DocumentId\" IS NOT NULL AND \"ServiceAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_DocumentId_UserId",
                table: "AclEntries",
                columns: new[] { "DocumentId", "UserId" },
                unique: true,
                filter: "\"DocumentId\" IS NOT NULL AND \"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_GroupId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "GroupId" },
                unique: true,
                filter: "\"RepositoryId\" IS NOT NULL AND \"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_ServiceAccountId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "ServiceAccountId" },
                unique: true,
                filter: "\"RepositoryId\" IS NOT NULL AND \"ServiceAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_UserId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "UserId" },
                unique: true,
                filter: "\"RepositoryId\" IS NOT NULL AND \"UserId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_ExactlyOneScope",
                table: "AclEntries",
                sql: "(CASE WHEN \"RepositoryId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"DocumentId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_AclEntries_Documents_DocumentId",
                table: "AclEntries",
                column: "DocumentId",
                principalTable: "Documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AclEntries_Documents_DocumentId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_DocumentId_GroupId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_DocumentId_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_DocumentId_UserId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_GroupId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_RepositoryId_UserId",
                table: "AclEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_ExactlyOneScope",
                table: "AclEntries");

            migrationBuilder.DropColumn(
                name: "BreaksInheritance",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                table: "AclEntries");

            migrationBuilder.AlterColumn<Guid>(
                name: "RepositoryId",
                table: "AclEntries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_GroupId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_ServiceAccountId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "ServiceAccountId" },
                unique: true,
                filter: "\"ServiceAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_UserId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");
        }
    }
}
