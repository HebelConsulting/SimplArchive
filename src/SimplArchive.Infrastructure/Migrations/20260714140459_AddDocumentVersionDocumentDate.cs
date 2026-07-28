using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVersionDocumentDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add nullable, backfill every existing version's issuing date to its filing date (CreatedAt's
            // UTC date), then enforce NOT NULL — so no lingering DB default and existing rows get real values.
            migrationBuilder.AddColumn<DateOnly>(
                name: "DocumentDate",
                table: "DocumentVersions",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"DocumentVersions\" SET \"DocumentDate\" = CAST(\"CreatedAt\" AT TIME ZONE 'UTC' AS date)");

            migrationBuilder.AlterColumn<DateOnly>(
                name: "DocumentDate",
                table: "DocumentVersions",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentDate",
                table: "DocumentVersions");
        }
    }
}
