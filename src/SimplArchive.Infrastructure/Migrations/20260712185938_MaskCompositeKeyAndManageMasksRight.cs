using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MaskCompositeKeyAndManageMasksRight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaskVersions_Masks_MaskId",
                table: "MaskVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryMasks_Masks_MaskId",
                table: "RepositoryMasks");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryMasks_MaskId",
                table: "RepositoryMasks");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryMasks_TenantId",
                table: "RepositoryMasks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Masks",
                table: "Masks");

            migrationBuilder.DropIndex(
                name: "IX_Masks_TenantId",
                table: "Masks");

            migrationBuilder.AddColumn<bool>(
                name: "CanManageMasks",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageMasks",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Masks",
                table: "Masks",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMasks_TenantId_MaskId",
                table: "RepositoryMasks",
                columns: new[] { "TenantId", "MaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaskVersions_TenantId_MaskId",
                table: "MaskVersions",
                columns: new[] { "TenantId", "MaskId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MaskVersions_Masks_TenantId_MaskId",
                table: "MaskVersions",
                columns: new[] { "TenantId", "MaskId" },
                principalTable: "Masks",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryMasks_Masks_TenantId_MaskId",
                table: "RepositoryMasks",
                columns: new[] { "TenantId", "MaskId" },
                principalTable: "Masks",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaskVersions_Masks_TenantId_MaskId",
                table: "MaskVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryMasks_Masks_TenantId_MaskId",
                table: "RepositoryMasks");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryMasks_TenantId_MaskId",
                table: "RepositoryMasks");

            migrationBuilder.DropIndex(
                name: "IX_MaskVersions_TenantId_MaskId",
                table: "MaskVersions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Masks",
                table: "Masks");

            migrationBuilder.DropColumn(
                name: "CanManageMasks",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageMasks",
                table: "ServiceAccounts");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Masks",
                table: "Masks",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMasks_MaskId",
                table: "RepositoryMasks",
                column: "MaskId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMasks_TenantId",
                table: "RepositoryMasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Masks_TenantId",
                table: "Masks",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaskVersions_Masks_MaskId",
                table: "MaskVersions",
                column: "MaskId",
                principalTable: "Masks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryMasks_Masks_MaskId",
                table: "RepositoryMasks",
                column: "MaskId",
                principalTable: "Masks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
