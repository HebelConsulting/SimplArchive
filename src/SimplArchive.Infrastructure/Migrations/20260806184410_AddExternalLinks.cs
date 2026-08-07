using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanCreateExternalLink",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowExternalLinks",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ExternalLinkDefaultAccesses",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "ExternalLinkMaxDays",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<bool>(
                name: "CanCreateExternalLink",
                table: "ServiceAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanCreateExternalLink",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ExternalLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxAccesses = table.Column<int>(type: "integer", nullable: true),
                    AccessCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalLinks", x => x.Id);
                    table.CheckConstraint("CK_ExternalLinks_ExactlyOneCreator", "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.ForeignKey(
                        name: "FK_ExternalLinks_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalLinks_ServiceAccounts_CreatedByServiceAccountId",
                        column: x => x.CreatedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalLinks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalLinks_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_CreatedByServiceAccountId",
                table: "ExternalLinks",
                column: "CreatedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_CreatedByUserId",
                table: "ExternalLinks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_DocumentId",
                table: "ExternalLinks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_TenantId_CreatedByUserId_ExpiresAt",
                table: "ExternalLinks",
                columns: new[] { "TenantId", "CreatedByUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_TenantId_DocumentId_ExpiresAt",
                table: "ExternalLinks",
                columns: new[] { "TenantId", "DocumentId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLinks_Token",
                table: "ExternalLinks",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalLinks");

            migrationBuilder.DropColumn(
                name: "CanCreateExternalLink",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AllowExternalLinks",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ExternalLinkDefaultAccesses",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ExternalLinkMaxDays",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CanCreateExternalLink",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "CanCreateExternalLink",
                table: "Groups");
        }
    }
}
