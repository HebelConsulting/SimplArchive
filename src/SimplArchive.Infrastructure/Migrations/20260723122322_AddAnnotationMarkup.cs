using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnotationMarkup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Height",
                table: "DocumentAnnotations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "DocumentAnnotations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "Width",
                table: "DocumentAnnotations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentAnnotations_Extent",
                table: "DocumentAnnotations",
                sql: "(\"Width\" IS NULL OR (\"Width\" >= -1 AND \"Width\" <= 1)) AND (\"Height\" IS NULL OR (\"Height\" >= -1 AND \"Height\" <= 1))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentAnnotations_ShapeExtent",
                table: "DocumentAnnotations",
                sql: "\"Kind\" = 0 OR (\"Width\" IS NOT NULL AND \"Height\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentAnnotations_Extent",
                table: "DocumentAnnotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentAnnotations_ShapeExtent",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "DocumentAnnotations");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "DocumentAnnotations");
        }
    }
}
