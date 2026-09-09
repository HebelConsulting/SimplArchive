using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultiResourceBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ResourceBookings_TenantId_BookingDocumentId",
                table: "ResourceBookings");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_TenantId_BookingDocumentId_ResourceDocumen~",
                table: "ResourceBookings",
                columns: new[] { "TenantId", "BookingDocumentId", "ResourceDocumentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ResourceBookings_TenantId_BookingDocumentId_ResourceDocumen~",
                table: "ResourceBookings");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_TenantId_BookingDocumentId",
                table: "ResourceBookings",
                columns: new[] { "TenantId", "BookingDocumentId" },
                unique: true);
        }
    }
}
