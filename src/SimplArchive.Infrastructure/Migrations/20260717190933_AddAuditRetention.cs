using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuditChainStartPreviousHash",
                table: "Tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "0000000000000000000000000000000000000000000000000000000000000000");

            migrationBuilder.AddColumn<long>(
                name: "AuditChainStartSequence",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuditLastPurgedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AuditRetentionDays",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 365);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuditChainStartPreviousHash",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditChainStartSequence",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditLastPurgedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditRetentionDays",
                table: "Tenants");
        }
    }
}
