using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StorageQuotaBytes",
                table: "Tenants",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StorageUsedBytes",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "DocumentVersions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageQuotaBytes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StorageUsedBytes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "DocumentVersions");
        }
    }
}
