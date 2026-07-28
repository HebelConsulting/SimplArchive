using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationEmailedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailedAt",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            // Treat every pre-existing notification as already emailed, so switching email on doesn't flood
            // recipients with a backlog — only notifications created after the upgrade get emailed.
            migrationBuilder.Sql(
                "UPDATE \"Notifications\" SET \"EmailedAt\" = CURRENT_TIMESTAMP WHERE \"EmailedAt\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailedAt",
                table: "Notifications");
        }
    }
}
