using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecurringBookingSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExceptionDates",
                table: "ResourceBookings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRule",
                table: "ResourceBookings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExceptionDates",
                table: "ResourceBlocks",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRule",
                table: "ResourceBlocks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExceptionDates",
                table: "ResourceAvailability",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRule",
                table: "ResourceAvailability",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExceptionDates",
                table: "ResourceBookings");

            migrationBuilder.DropColumn(
                name: "RecurrenceRule",
                table: "ResourceBookings");

            migrationBuilder.DropColumn(
                name: "ExceptionDates",
                table: "ResourceBlocks");

            migrationBuilder.DropColumn(
                name: "RecurrenceRule",
                table: "ResourceBlocks");

            migrationBuilder.DropColumn(
                name: "ExceptionDates",
                table: "ResourceAvailability");

            migrationBuilder.DropColumn(
                name: "RecurrenceRule",
                table: "ResourceAvailability");
        }
    }
}
