using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCanMoveRightAndDocumentReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries");

            migrationBuilder.AddColumn<bool>(
                name: "CanMove",
                table: "AclEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DocumentReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentReferences", x => x.Id);
                    table.CheckConstraint("CK_DocumentReferences_ExactlyOneCreator", "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_DocumentReferences_NotSelf", "\"TargetDocumentId\" <> \"ParentFolderId\"");
                    table.ForeignKey(
                        name: "FK_DocumentReferences_Documents_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentReferences_Documents_TargetDocumentId",
                        column: x => x.TargetDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentReferences_ServiceAccounts_CreatedByServiceAccountId",
                        column: x => x.CreatedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentReferences_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentReferences_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries",
                sql: "\"CanSee\" OR \"CanReadContent\" OR \"CanEditContent\" OR \"CanEditIndexData\" OR \"CanDelete\" OR \"CanCreateSubItems\" OR \"CanManagePermissions\" OR \"CanMove\"");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentReferences_CreatedByServiceAccountId",
                table: "DocumentReferences",
                column: "CreatedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentReferences_CreatedByUserId",
                table: "DocumentReferences",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentReferences_ParentFolderId",
                table: "DocumentReferences",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentReferences_TargetDocumentId",
                table: "DocumentReferences",
                column: "TargetDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentReferences_TenantId_ParentFolderId_TargetDocumentId",
                table: "DocumentReferences",
                columns: new[] { "TenantId", "ParentFolderId", "TargetDocumentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentReferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries");

            migrationBuilder.DropColumn(
                name: "CanMove",
                table: "AclEntries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries",
                sql: "\"CanSee\" OR \"CanReadContent\" OR \"CanEditContent\" OR \"CanEditIndexData\" OR \"CanDelete\" OR \"CanCreateSubItems\" OR \"CanManagePermissions\"");
        }
    }
}
