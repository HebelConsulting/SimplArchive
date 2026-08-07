using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageMention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatMessageMentions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessageMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessageMentions_ChatMessages_ChatMessageId",
                        column: x => x.ChatMessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMessageMentions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChatMessageMentions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageMentions_ChatMessageId_UserId",
                table: "ChatMessageMentions",
                columns: new[] { "ChatMessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageMentions_TenantId_UserId_CreatedAt",
                table: "ChatMessageMentions",
                columns: new[] { "TenantId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageMentions_UserId",
                table: "ChatMessageMentions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessageMentions");
        }
    }
}
