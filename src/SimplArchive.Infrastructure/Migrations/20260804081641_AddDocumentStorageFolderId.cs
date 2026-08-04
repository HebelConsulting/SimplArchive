using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentStorageFolderId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The app assigns StorageFolderId at document creation (ADR 0530); this migration-only default backfills
            // EXISTING rows with a distinct random GUID each (not a shared Guid.Empty). Postgres-side; the model
            // carries no default, so new rows always get the app-assigned value.
            migrationBuilder.AddColumn<Guid>(
                name: "StorageFolderId",
                table: "Documents",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageFolderId",
                table: "Documents");
        }
    }
}
