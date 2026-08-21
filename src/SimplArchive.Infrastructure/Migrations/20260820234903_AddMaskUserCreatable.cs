using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskUserCreatable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue TRUE, hand-corrected from the `false` the tooling generated: the backfill decides
            // what every EXISTING mask becomes, and a tenant-authored mask should be creatable (#678). The
            // seeder's heal then sets false for the six the application provisions — Repository, User Folder,
            // My Documents, Mailbox, IMAP Special and Notebook — in the same startup, since migrations run
            // before seeding.
            //
            // This default is a BACKFILL, not a source of values: the model carries no HasDefaultValue, and
            // the CLR initializer (= true) is what new rows get. Once this migration has run, the store
            // default has done its whole job.
            migrationBuilder.AddColumn<bool>(
                name: "UserCreatable",
                table: "Masks",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserCreatable",
                table: "Masks");
        }
    }
}
