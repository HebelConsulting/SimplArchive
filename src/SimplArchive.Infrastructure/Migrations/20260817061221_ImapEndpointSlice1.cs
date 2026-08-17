using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ImapEndpointSlice1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImapPasswordHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ImapShowAllDocuments",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ImapMailboxes",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UidValidity = table.Column<int>(type: "integer", nullable: false),
                    NextUid = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImapMailboxes", x => x.FolderId);
                    table.ForeignKey(
                        name: "FK_ImapMailboxes_Documents_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImapMailboxes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImapMessageUids",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Uid = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImapMessageUids", x => new { x.FolderId, x.DocumentId });
                    table.ForeignKey(
                        name: "FK_ImapMessageUids_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImapMessageUids_ImapMailboxes_FolderId",
                        column: x => x.FolderId,
                        principalTable: "ImapMailboxes",
                        principalColumn: "FolderId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImapMessageUids_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImapMailboxes_TenantId",
                table: "ImapMailboxes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ImapMessageUids_DocumentId",
                table: "ImapMessageUids",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ImapMessageUids_FolderId_Uid",
                table: "ImapMessageUids",
                columns: new[] { "FolderId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImapMessageUids_TenantId",
                table: "ImapMessageUids",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImapMessageUids");

            migrationBuilder.DropTable(
                name: "ImapMailboxes");

            migrationBuilder.DropColumn(
                name: "ImapPasswordHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ImapShowAllDocuments",
                table: "Users");
        }
    }
}
