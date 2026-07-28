using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAdministrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformAdministrators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OpenIddictApplicationClientId = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAdministrators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdministrators_Name",
                table: "PlatformAdministrators",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdministrators_OpenIddictApplicationClientId",
                table: "PlatformAdministrators",
                column: "OpenIddictApplicationClientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformAdministrators");
        }
    }
}
