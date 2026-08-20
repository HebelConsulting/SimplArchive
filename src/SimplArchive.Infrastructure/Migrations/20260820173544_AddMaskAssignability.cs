using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskAssignability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFolderMask",
                table: "Masks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MaskFileExtensions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Extension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskFileExtensions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaskFileExtensions_Masks_TenantId_MaskId",
                        columns: x => new { x.TenantId, x.MaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaskFileExtensions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaskFileExtensions_TenantId_Extension",
                table: "MaskFileExtensions",
                columns: new[] { "TenantId", "Extension" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaskFileExtensions_TenantId_MaskId",
                table: "MaskFileExtensions",
                columns: new[] { "TenantId", "MaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaskFileExtensions");

            migrationBuilder.DropColumn(
                name: "IsFolderMask",
                table: "Masks");
        }
    }
}
