using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExportImportRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanExport",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanImport",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanExport",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanImport",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanExport",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanImport",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanExport",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanImport",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanExport",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "CanImport",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "CanExport",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CanImport",
                table: "Groups");
        }
    }
}
