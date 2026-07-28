using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FieldDefinitions_Masks_MaskId",
                table: "FieldDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Masks_TenantId_Name",
                table: "Masks");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Masks");

            migrationBuilder.RenameColumn(
                name: "MaskId",
                table: "FieldDefinitions",
                newName: "MaskVersionId");

            migrationBuilder.RenameIndex(
                name: "IX_FieldDefinitions_MaskId_Name",
                table: "FieldDefinitions",
                newName: "IX_FieldDefinitions_MaskVersionId_Name");

            migrationBuilder.CreateTable(
                name: "MaskVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaskVersions_Masks_MaskId",
                        column: x => x.MaskId,
                        principalTable: "Masks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaskVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Masks_TenantId",
                table: "Masks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MaskVersions_MaskId_VersionNumber",
                table: "MaskVersions",
                columns: new[] { "MaskId", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_MaskVersions_TenantId_Name",
                table: "MaskVersions",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_FieldDefinitions_MaskVersions_MaskVersionId",
                table: "FieldDefinitions",
                column: "MaskVersionId",
                principalTable: "MaskVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FieldDefinitions_MaskVersions_MaskVersionId",
                table: "FieldDefinitions");

            migrationBuilder.DropTable(
                name: "MaskVersions");

            migrationBuilder.DropIndex(
                name: "IX_Masks_TenantId",
                table: "Masks");

            migrationBuilder.RenameColumn(
                name: "MaskVersionId",
                table: "FieldDefinitions",
                newName: "MaskId");

            migrationBuilder.RenameIndex(
                name: "IX_FieldDefinitions_MaskVersionId_Name",
                table: "FieldDefinitions",
                newName: "IX_FieldDefinitions_MaskId_Name");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Masks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Masks_TenantId_Name",
                table: "Masks",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FieldDefinitions_Masks_MaskId",
                table: "FieldDefinitions",
                column: "MaskId",
                principalTable: "Masks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
