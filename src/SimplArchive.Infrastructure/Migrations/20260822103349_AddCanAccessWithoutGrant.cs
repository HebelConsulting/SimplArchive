using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCanAccessWithoutGrant : Migration
    {
        // Personal spaces become private, and access without a grant becomes a right (ADR 0670).
        //
        // DELIBERATELY SCHEMA-ONLY — no data backfill, decided with the owner on the grounds that every
        // environment is recreated from empty volumes today (the kiosk resets nightly; there is no user base).
        // Two consequences that are invisible unless written down, so they are written down here:
        //
        //   * Existing admins get CanAccessWithoutGrant = false, so nobody holds the x-ray until it is granted.
        //     This fails CLOSED and is harmless.
        //   * Existing documents get PersonalRootOwnerId = NULL, which reads as "not inside a personal space",
        //     so the tenant-admin bypass STILL APPLIES to anything predating this migration. That fails OPEN.
        //     It is acceptable only while no long-lived volume exists. The day one does, the fix is a data-only
        //     migration walking ParentId to each root and setting the column — the walk omitted here.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanAccessWithoutGrant",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanAccessWithoutGrant",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanAccessWithoutGrant",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PersonalRootOwnerId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_PersonalRootOwnerId",
                table: "Documents",
                columns: new[] { "TenantId", "PersonalRootOwnerId" },
                filter: "\"PersonalRootOwnerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_PersonalRootOwnerId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "CanAccessWithoutGrant",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanAccessWithoutGrant",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "CanAccessWithoutGrant",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PersonalRootOwnerId",
                table: "Documents");
        }
    }
}
