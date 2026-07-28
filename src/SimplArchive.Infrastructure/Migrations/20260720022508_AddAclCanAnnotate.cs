using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAclCanAnnotate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries");

            migrationBuilder.AddColumn<bool>(
                name: "CanAnnotate",
                table: "AclEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Preserve the pre-CanAnnotate behavior ("anyone who can read the content can annotate it"): every
            // existing grant with CanReadContent gets CanAnnotate so nobody loses annotation on upgrade (ADR
            // "CanAnnotate right"). SQLite (tests) has no real boolean literal but treats 1/0 as true/false.
            migrationBuilder.Sql("UPDATE \"AclEntries\" SET \"CanAnnotate\" = TRUE WHERE \"CanReadContent\" = TRUE;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries",
                sql: "\"CanSee\" OR \"CanReadContent\" OR \"CanEditContent\" OR \"CanEditIndexData\" OR \"CanDelete\" OR \"CanCreateSubItems\" OR \"CanManagePermissions\" OR \"CanMove\" OR \"CanAnnotate\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries");

            migrationBuilder.DropColumn(
                name: "CanAnnotate",
                table: "AclEntries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries",
                sql: "\"CanSee\" OR \"CanReadContent\" OR \"CanEditContent\" OR \"CanEditIndexData\" OR \"CanDelete\" OR \"CanCreateSubItems\" OR \"CanManagePermissions\" OR \"CanMove\"");
        }
    }
}
