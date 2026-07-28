using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAclEntryAtLeastOneRightCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries",
                sql: "\"CanSee\" OR \"CanReadContent\" OR \"CanEditContent\" OR \"CanEditIndexData\" OR \"CanDelete\" OR \"CanCreateSubItems\" OR \"CanManagePermissions\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AclEntries_AtLeastOneRight",
                table: "AclEntries");
        }
    }
}
