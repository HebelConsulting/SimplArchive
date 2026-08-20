using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskContainment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdmitsNoSubfolders",
                table: "Masks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AdmitsOnlyDeclaredChildren",
                table: "Masks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MaskAdmittedChildren",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderMaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildMaskId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskAdmittedChildren", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaskAdmittedChildren_Masks_TenantId_ChildMaskId",
                        columns: x => new { x.TenantId, x.ChildMaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaskAdmittedChildren_Masks_TenantId_FolderMaskId",
                        columns: x => new { x.TenantId, x.FolderMaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaskAdmittedChildren_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaskAllowedParents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentMaskId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskAllowedParents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaskAllowedParents_Masks_TenantId_MaskId",
                        columns: x => new { x.TenantId, x.MaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaskAllowedParents_Masks_TenantId_ParentMaskId",
                        columns: x => new { x.TenantId, x.ParentMaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaskAllowedParents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaskAdmittedChildren_TenantId_ChildMaskId",
                table: "MaskAdmittedChildren",
                columns: new[] { "TenantId", "ChildMaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaskAdmittedChildren_TenantId_FolderMaskId_ChildMaskId",
                table: "MaskAdmittedChildren",
                columns: new[] { "TenantId", "FolderMaskId", "ChildMaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaskAllowedParents_TenantId_MaskId_ParentMaskId",
                table: "MaskAllowedParents",
                columns: new[] { "TenantId", "MaskId", "ParentMaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaskAllowedParents_TenantId_ParentMaskId",
                table: "MaskAllowedParents",
                columns: new[] { "TenantId", "ParentMaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaskAdmittedChildren");

            migrationBuilder.DropTable(
                name: "MaskAllowedParents");

            migrationBuilder.DropColumn(
                name: "AdmitsNoSubfolders",
                table: "Masks");

            migrationBuilder.DropColumn(
                name: "AdmitsOnlyDeclaredChildren",
                table: "Masks");
        }
    }
}
