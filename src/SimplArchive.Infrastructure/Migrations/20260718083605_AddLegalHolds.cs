using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegalHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    PlacedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlacedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegalHolds_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalHolds_Users_PlacedByUserId",
                        column: x => x.PlacedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalHolds_Users_ReleasedByUserId",
                        column: x => x.ReleasedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegalHoldItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalHoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHoldItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegalHoldItems_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalHoldItems_LegalHolds_LegalHoldId",
                        column: x => x.LegalHoldId,
                        principalTable: "LegalHolds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LegalHoldItems_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalHoldItems_DocumentId",
                table: "LegalHoldItems",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHoldItems_LegalHoldId",
                table: "LegalHoldItems",
                column: "LegalHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHoldItems_TenantId_LegalHoldId_DocumentId",
                table: "LegalHoldItems",
                columns: new[] { "TenantId", "LegalHoldId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalHolds_PlacedByUserId",
                table: "LegalHolds",
                column: "PlacedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHolds_ReleasedByUserId",
                table: "LegalHolds",
                column: "ReleasedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHolds_TenantId_PlacedAt_Id",
                table: "LegalHolds",
                columns: new[] { "TenantId", "PlacedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegalHoldItems");

            migrationBuilder.DropTable(
                name: "LegalHolds");
        }
    }
}
