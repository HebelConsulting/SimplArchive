using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameCanManageIntrayesToIntrays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CanManageIntrayes",
                table: "Users",
                newName: "CanManageIntrays");

            migrationBuilder.RenameColumn(
                name: "CanManageIntrayes",
                table: "ServiceAccounts",
                newName: "CanManageIntrays");

            migrationBuilder.RenameColumn(
                name: "CanManageIntrayes",
                table: "Groups",
                newName: "CanManageIntrays");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CanManageIntrays",
                table: "Users",
                newName: "CanManageIntrayes");

            migrationBuilder.RenameColumn(
                name: "CanManageIntrays",
                table: "ServiceAccounts",
                newName: "CanManageIntrayes");

            migrationBuilder.RenameColumn(
                name: "CanManageIntrays",
                table: "Groups",
                newName: "CanManageIntrayes");
        }
    }
}
