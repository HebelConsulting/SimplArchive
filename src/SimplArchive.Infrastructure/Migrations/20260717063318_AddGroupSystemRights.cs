using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupSystemRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanImpersonate",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanLegalHold",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageClassification",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageMasks",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageRepositories",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageServiceAccounts",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageUsers",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanOverrideCheckout",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanResetMfa",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTenantAdmin",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanImpersonate",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanLegalHold",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanManageClassification",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanManageMasks",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanManageRepositories",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanManageServiceAccounts",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanManageUsers",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanOverrideCheckout",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanResetMfa",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "IsTenantAdmin",
                table: "Groups");
        }
    }
}
