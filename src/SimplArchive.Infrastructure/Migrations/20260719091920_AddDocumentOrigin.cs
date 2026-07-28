using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginDocumentId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginTenantId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_OriginTenantId_OriginDocumentId",
                table: "Documents",
                columns: new[] { "TenantId", "OriginTenantId", "OriginDocumentId" },
                unique: true,
                filter: "\"OriginDocumentId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_OriginTenantId_OriginDocumentId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "OriginDocumentId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "OriginTenantId",
                table: "Documents");
        }
    }
}
