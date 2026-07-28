using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskAndEavEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Masks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Masks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Masks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FieldDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<int>(type: "integer", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    IsUnique = table.Column<bool>(type: "boolean", nullable: false),
                    FormatPattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MaxTextLength = table.Column<int>(type: "integer", nullable: true),
                    MinValue = table.Column<string>(type: "text", nullable: true),
                    MaxValue = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldDefinitions_Masks_MaskId",
                        column: x => x.MaskId,
                        principalTable: "Masks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FieldDefinitions_Tenants_TenantId",
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
                        name: "FK_RepositoryMasks_Masks_MaskId",
                        column: x => x.MaskId,
                        principalTable: "Masks",
                        principalColumn: "Id",
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

            migrationBuilder.CreateTable(
                name: "FieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldValues_FieldDefinitions_FieldDefinitionId",
                        column: x => x.FieldDefinitionId,
                        principalTable: "FieldDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FieldValues_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FieldValues_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitions_MaskId",
                table: "FieldDefinitions",
                column: "MaskId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitions_TenantId",
                table: "FieldDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_DocumentId_FieldDefinitionId",
                table: "FieldValues",
                columns: new[] { "DocumentId", "FieldDefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_FieldDefinitionId",
                table: "FieldValues",
                column: "FieldDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_RepositoryId",
                table: "FieldValues",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_TenantId",
                table: "FieldValues",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Masks_TenantId",
                table: "Masks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMasks_MaskId",
                table: "RepositoryMasks",
                column: "MaskId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMasks_TenantId",
                table: "RepositoryMasks",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldValues");

            migrationBuilder.DropTable(
                name: "RepositoryMasks");

            migrationBuilder.DropTable(
                name: "FieldDefinitions");

            migrationBuilder.DropTable(
                name: "Masks");
        }
    }
}
