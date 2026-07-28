using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSystemLevelRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanImpersonate",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanLegalHold",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageClassification",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageRepositories",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageServiceAccounts",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanOverrideCheckout",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanResetMfa",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanImpersonate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanLegalHold",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageClassification",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageRepositories",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageServiceAccounts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanOverrideCheckout",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanResetMfa",
                table: "Users");
        }
    }
}
