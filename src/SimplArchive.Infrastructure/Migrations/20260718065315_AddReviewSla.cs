using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAt",
                table: "WorkflowStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscalatedAt",
                table: "WorkflowStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderSentAt",
                table: "WorkflowStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewSlaDays",
                table: "MaskVersions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueAt",
                table: "WorkflowStates");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                table: "WorkflowStates");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "WorkflowStates");

            migrationBuilder.DropColumn(
                name: "ReviewSlaDays",
                table: "MaskVersions");
        }
    }
}
