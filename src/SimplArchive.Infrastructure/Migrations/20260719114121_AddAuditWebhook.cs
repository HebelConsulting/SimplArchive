using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AuditWebhookDeliveredThrough",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: -1L);

            migrationBuilder.AddColumn<string>(
                name: "AuditWebhookSecret",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuditWebhookUrl",
                table: "Tenants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuditWebhookDeliveredThrough",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditWebhookSecret",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AuditWebhookUrl",
                table: "Tenants");
        }
    }
}
