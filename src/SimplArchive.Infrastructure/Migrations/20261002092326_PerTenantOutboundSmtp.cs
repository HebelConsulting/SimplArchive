using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerTenantOutboundSmtp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SmtpFromAddress",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpFromName",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpPassword",
                table: "Tenants",
                type: "text",
                nullable: true);

            // 587, NOT the 0 EF generates from int's CLR default. The entity initialises to 587, but that
            // initialiser cannot reach rows that already exist — so every tenant in an upgraded installation
            // would have carried port 0, and the first one to fill in a host without touching the port would
            // have failed to connect with nothing on screen to explain it. The approved schema says 587.
            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 587);

            migrationBuilder.AddColumn<bool>(
                name: "SmtpUseStartTls",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUser",
                table: "Tenants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmtpFromAddress",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpFromName",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpPassword",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpUseStartTls",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpUser",
                table: "Tenants");
        }
    }
}
