using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCutInboxUploadsAtPatchCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue TRUE, not the generated `false`: cutting a batch scan at its separator sheets is on
            // unless a user turns it off (#492), so every EXISTING user has to be backfilled with it on. EF
            // generated `false` only because that is the CLR default for a bool.
            //
            // The sibling migration AddUserDeskewInboxUploads says the rest, and it applies here unchanged: the
            // DB-level DEFAULT this leaves behind is never consulted by EF, because the MODEL deliberately
            // carries no HasDefaultValue — a store default there would make `false` unstorable, since EF omits
            // a property equal to the CLR default and the row would come back `true`.
            migrationBuilder.AddColumn<bool>(
                name: "CutInboxUploadsAtPatchCodes",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CutInboxUploadsAtPatchCodes",
                table: "Users");
        }
    }
}
