using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditWebhookHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditWebhookConsecutiveFailures",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AuditWebhookLastError",
                table: "Tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuditWebhookLastFailureAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuditWebhookLastSuccessAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuditWebhookNextAttemptAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuditWebhookConsecutiveFailures",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditWebhookLastError",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditWebhookLastFailureAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditWebhookLastSuccessAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditWebhookNextAttemptAt",
                table: "Tenants");
        }
    }
}
