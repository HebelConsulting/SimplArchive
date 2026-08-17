using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationEmailRetryBookkeeping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailAttempts",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailFailedAt",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EmailedAt_EmailFailedAt_Id",
                table: "Notifications",
                columns: new[] { "EmailedAt", "EmailFailedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_EmailedAt_EmailFailedAt_Id",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailAttempts",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailFailedAt",
                table: "Notifications");
        }
    }
}
