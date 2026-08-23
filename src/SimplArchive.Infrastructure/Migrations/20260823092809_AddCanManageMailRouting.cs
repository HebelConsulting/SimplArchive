using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCanManageMailRouting : Migration
    {
        // The mail-routing right (#703): write a Mailbox's address list, delete or restore a mailbox.
        //
        // DELIBERATELY SCHEMA-ONLY, following AddCanAccessWithoutGrant's owner-decided precedent: no backfill,
        // so every existing principal reads false and nobody may route mail until granted. Fails CLOSED and is
        // harmless — freshly provisioned tenants get their founding admin the right from
        // TenantProvisioningService, and every long-lived environment today is recreated from empty volumes.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanManageMailRouting",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageMailRouting",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageMailRouting",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanManageMailRouting",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageMailRouting",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "CanManageMailRouting",
                table: "Groups");
        }
    }
}
