using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    BlockedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    BlockedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceBlocks", x => x.Id);
                    table.CheckConstraint("CK_ResourceBlocks_ExactlyOneCreator", "(CASE WHEN \"BlockedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"BlockedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_ResourceBlocks_WindowHasExtent", "\"StartsAtUtc\" < \"EndsAtUtc\"");
                    table.ForeignKey(
                        name: "FK_ResourceBlocks_Documents_ResourceDocumentId",
                        column: x => x.ResourceDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceBlocks_ServiceAccounts_BlockedByServiceAccountId",
                        column: x => x.BlockedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceBlocks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceBlocks_Users_BlockedByUserId",
                        column: x => x.BlockedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBlocks_BlockDocumentId",
                table: "ResourceBlocks",
                column: "BlockDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBlocks_BlockedByServiceAccountId",
                table: "ResourceBlocks",
                column: "BlockedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBlocks_BlockedByUserId",
                table: "ResourceBlocks",
                column: "BlockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBlocks_ResourceDocumentId",
                table: "ResourceBlocks",
                column: "ResourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBlocks_TenantId_BlockDocumentId",
                table: "ResourceBlocks",
                columns: new[] { "TenantId", "BlockDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBlocks_TenantId_ResourceDocumentId_StartsAtUtc",
                table: "ResourceBlocks",
                columns: new[] { "TenantId", "ResourceDocumentId", "StartsAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceBlocks");
        }
    }
}
