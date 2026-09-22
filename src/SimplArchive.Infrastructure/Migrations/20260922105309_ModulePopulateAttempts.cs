using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModulePopulateAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModulePopulateAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModulePopulateAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModulePopulateAttempts_Documents_SubjectDocumentId",
                        column: x => x.SubjectDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ModulePopulateAttempts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModulePopulateAttempts_SubjectDocumentId",
                table: "ModulePopulateAttempts",
                column: "SubjectDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ModulePopulateAttempts_TenantId_MachineId_SubjectDocumentId",
                table: "ModulePopulateAttempts",
                columns: new[] { "TenantId", "MachineId", "SubjectDocumentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModulePopulateAttempts");
        }
    }
}
