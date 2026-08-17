using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDeskewIntrayUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue TRUE, not the generated `false`: straightening crooked scans is on unless a user
            // turns it off (#491), so every EXISTING user has to be backfilled with it on. EF generated `false`
            // only because that is the CLR default for a bool.
            //
            // This leaves a DB-level DEFAULT on the column, which is harmless and never consulted by EF: the
            // MODEL deliberately carries no HasDefaultValue, so EF always writes the property explicitly on
            // INSERT. That is the whole point — a store default in the model would make `false` unstorable,
            // because EF omits a property equal to the CLR default and the row would come back `true`.
            migrationBuilder.AddColumn<bool>(
                name: "DeskewIntrayUploads",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeskewIntrayUploads",
                table: "Users");
        }
    }
}
