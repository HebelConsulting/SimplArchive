using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowRefusedAttachmentChatEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages",
                sql: "(\"Kind\" = 0 AND \"DocumentVersionId\" IS NULL) OR (\"Kind\" IN (1, 2) AND \"DocumentVersionId\" IS NOT NULL) OR (\"Kind\" = 3 AND \"DocumentVersionId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages",
                sql: "(\"Kind\" = 0 AND \"DocumentVersionId\" IS NULL) OR (\"Kind\" IN (1, 2) AND \"DocumentVersionId\" IS NOT NULL)");
        }
    }
}
