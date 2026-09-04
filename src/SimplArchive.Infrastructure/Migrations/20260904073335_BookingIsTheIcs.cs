using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BookingIsTheIcs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceBookings_Documents_BookingDocumentId",
                table: "ResourceBookings");

            migrationBuilder.DropColumn(
                name: "AppointmentDocumentId",
                table: "ResourceBookings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppointmentDocumentId",
                table: "ResourceBookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceBookings_Documents_BookingDocumentId",
                table: "ResourceBookings",
                column: "BookingDocumentId",
                principalTable: "Documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
