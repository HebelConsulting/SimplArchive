using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ResourceAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceAvailability",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OfferedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OfferedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceAvailability", x => x.Id);
                    table.CheckConstraint("CK_ResourceAvailability_ExactlyOneOfferer", "(CASE WHEN \"OfferedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"OfferedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_ResourceAvailability_WindowHasExtent", "\"StartsAtUtc\" < \"EndsAtUtc\"");
                    table.ForeignKey(
                        name: "FK_ResourceAvailability_Documents_ResourceDocumentId",
                        column: x => x.ResourceDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceAvailability_ServiceAccounts_OfferedByServiceAccoun~",
                        column: x => x.OfferedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceAvailability_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceAvailability_Users_OfferedByUserId",
                        column: x => x.OfferedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAvailability_OfferedByServiceAccountId",
                table: "ResourceAvailability",
                column: "OfferedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAvailability_OfferedByUserId",
                table: "ResourceAvailability",
                column: "OfferedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAvailability_ResourceDocumentId",
                table: "ResourceAvailability",
                column: "ResourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAvailability_TenantId_ResourceDocumentId_StartsAtUtc",
                table: "ResourceAvailability",
                columns: new[] { "TenantId", "ResourceDocumentId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAvailability_TenantId_WindowDocumentId",
                table: "ResourceAvailability",
                columns: new[] { "TenantId", "WindowDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAvailability_WindowDocumentId",
                table: "ResourceAvailability",
                column: "WindowDocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceAvailability");
        }
    }
}
