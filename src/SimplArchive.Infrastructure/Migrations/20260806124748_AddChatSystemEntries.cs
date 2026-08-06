using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSystemEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CommentKind",
                table: "DocumentVersions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentVersionId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ChatMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_DocumentVersionId",
                table: "ChatMessages",
                column: "DocumentVersionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages",
                sql: "(\"Kind\" IN (0, 1) AND \"DocumentVersionId\" IS NULL) OR (\"Kind\" IN (2, 3) AND \"DocumentVersionId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessages_DocumentVersions_DocumentVersionId",
                table: "ChatMessages",
                column: "DocumentVersionId",
                principalTable: "DocumentVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessages_DocumentVersions_DocumentVersionId",
                table: "ChatMessages");

            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_DocumentVersionId",
                table: "ChatMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatMessages_KindVersionPairing",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "CommentKind",
                table: "DocumentVersions");

            migrationBuilder.DropColumn(
                name: "DocumentVersionId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ChatMessages");
        }
    }
}
