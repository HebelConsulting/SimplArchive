using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryDocumentUnification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AclEntries_Repositories_RepositoryId",
                table: "AclEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Repositories_RepositoryId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_FieldValues_Repositories_RepositoryId",
                table: "FieldValues");

            migrationBuilder.DropTable(
                name: "RepositoryMasks");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_FieldValues_RepositoryId",
                table: "FieldValues");

            migrationBuilder.DropIndex(
                name: "IX_Documents_RepositoryId",
                table: "Documents");

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
                name: "RepositoryId",
                table: "FieldValues");

            migrationBuilder.DropColumn(
                name: "IsUnique",
                table: "FieldDefinitions");

            migrationBuilder.DropColumn(
                name: "RepositoryId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "RepositoryId",
                table: "AclEntries");

            migrationBuilder.AlterColumn<Guid>(
                name: "DocumentId",
                table: "AclEntries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_DocumentId_GroupId",
                table: "AclEntries",
                columns: new[] { "DocumentId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_DocumentId_ServiceAccountId",
                table: "AclEntries",
                columns: new[] { "DocumentId", "ServiceAccountId" },
                unique: true,
                filter: "\"ServiceAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_DocumentId_UserId",
                table: "AclEntries",
                columns: new[] { "DocumentId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AclEntries_DocumentId_GroupId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_DocumentId_ServiceAccountId",
                table: "AclEntries");

            migrationBuilder.DropIndex(
                name: "IX_AclEntries_DocumentId_UserId",
                table: "AclEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryId",
                table: "FieldValues",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsUnique",
                table: "FieldDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryId",
                table: "Documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<Guid>(
                name: "DocumentId",
                table: "AclEntries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryId",
                table: "AclEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                    table.CheckConstraint("CK_Repositories_Status_DeactivatedAt", "(\"Status\" = 0 AND \"DeactivatedAt\" IS NULL) OR (\"Status\" = 1 AND \"DeactivatedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Repositories_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryMasks",
                columns: table => new
                {
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryMasks", x => new { x.RepositoryId, x.MaskId });
                    table.ForeignKey(
                        name: "FK_RepositoryMasks_Masks_TenantId_MaskId",
                        columns: x => new { x.TenantId, x.MaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RepositoryMasks_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RepositoryMasks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_RepositoryId",
                table: "FieldValues",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_RepositoryId",
                table: "Documents",
                column: "RepositoryId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_TenantId_Name",
                table: "Repositories",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMasks_TenantId_MaskId",
                table: "RepositoryMasks",
                columns: new[] { "TenantId", "MaskId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AclEntries_Repositories_RepositoryId",
                table: "AclEntries",
                column: "RepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Repositories_RepositoryId",
                table: "Documents",
                column: "RepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FieldValues_Repositories_RepositoryId",
                table: "FieldValues",
                column: "RepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
