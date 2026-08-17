using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRotateIntrayUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue TRUE, not the generated `false`: correcting a page that arrived 90 or 180 degrees
            // round is on unless a user turns it off, so every EXISTING user has to be backfilled with it on.
            // EF generated `false` only because that is the CLR default for a bool.
            //
            // This setting is SPLIT OUT of DeskewIntrayUploads rather than replacing it: the two corrections
            // cost differently (rotation on a PDF is only the /Rotate attribute, so it is lossless), and the
            // TIFF-only gate deskew needs had silently been inherited by rotation. Existing users keep their
            // deskew choice untouched and gain rotation on, which is what the single flag already implied.
            //
            // The DB-level DEFAULT this leaves behind is never consulted by EF, because the MODEL deliberately
            // carries no HasDefaultValue — a store default there would make `false` unstorable, since EF omits
            // a property equal to the CLR default and the row would come back `true`.
            migrationBuilder.AddColumn<bool>(
                name: "RotateIntrayUploads",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RotateIntrayUploads",
                table: "Users");
        }
    }
}
