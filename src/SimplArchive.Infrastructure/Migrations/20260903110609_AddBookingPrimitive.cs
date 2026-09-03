using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPrimitive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBookable",
                table: "Masks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ResourceBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    BookedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceBookings", x => x.Id);
                    table.CheckConstraint("CK_ResourceBookings_ExactlyOneBooker", "(CASE WHEN \"BookedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"BookedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_ResourceBookings_SlotHasExtent", "\"StartsAtUtc\" < \"EndsAtUtc\"");
                    table.ForeignKey(
                        name: "FK_ResourceBookings_Documents_BookingDocumentId",
                        column: x => x.BookingDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceBookings_Documents_ResourceDocumentId",
                        column: x => x.ResourceDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceBookings_ServiceAccounts_BookedByServiceAccountId",
                        column: x => x.BookedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceBookings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceBookings_Users_BookedByUserId",
                        column: x => x.BookedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_BookedByServiceAccountId",
                table: "ResourceBookings",
                column: "BookedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_BookedByUserId",
                table: "ResourceBookings",
                column: "BookedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_BookingDocumentId",
                table: "ResourceBookings",
                column: "BookingDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_ResourceDocumentId",
                table: "ResourceBookings",
                column: "ResourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_TenantId_BookingDocumentId",
                table: "ResourceBookings",
                columns: new[] { "TenantId", "BookingDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_TenantId_ResourceDocumentId_StartsAtUtc",
                table: "ResourceBookings",
                columns: new[] { "TenantId", "ResourceDocumentId", "StartsAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceBookings");

            migrationBuilder.DropColumn(
                name: "IsBookable",
                table: "Masks");
        }
    }
}
