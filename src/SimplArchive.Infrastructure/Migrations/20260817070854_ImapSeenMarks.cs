using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ImapSeenMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImapSeenMarks",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImapSeenMarks", x => new { x.UserId, x.DocumentId });
                    table.ForeignKey(
                        name: "FK_ImapSeenMarks_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImapSeenMarks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImapSeenMarks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImapSeenMarks_DocumentId",
                table: "ImapSeenMarks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ImapSeenMarks_TenantId_DocumentId",
                table: "ImapSeenMarks",
                columns: new[] { "TenantId", "DocumentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImapSeenMarks");
        }
    }
}
