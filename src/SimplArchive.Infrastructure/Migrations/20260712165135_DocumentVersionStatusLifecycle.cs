using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DocumentVersionStatusLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "VersionNumber",
                table: "DocumentVersions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Hash",
                table: "DocumentVersions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "DocumentVersions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentVersions_Status_VersionNumber_Sha256Hash",
                table: "DocumentVersions",
                sql: "(\"Status\" = 0 AND \"VersionNumber\" IS NULL AND \"Sha256Hash\" IS NULL) OR (\"Status\" = 1 AND \"VersionNumber\" IS NOT NULL AND \"Sha256Hash\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentVersions_Status_VersionNumber_Sha256Hash",
                table: "DocumentVersions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "DocumentVersions");

            migrationBuilder.AlterColumn<int>(
                name: "VersionNumber",
                table: "DocumentVersions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Hash",
                table: "DocumentVersions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
