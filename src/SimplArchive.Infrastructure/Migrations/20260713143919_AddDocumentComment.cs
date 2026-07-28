using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentComments", x => x.Id);
                    table.CheckConstraint("CK_DocumentComments_ExactlyOneCreator", "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.ForeignKey(
                        name: "FK_DocumentComments_DocumentComments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "DocumentComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentComments_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentComments_ServiceAccounts_CreatedByServiceAccountId",
                        column: x => x.CreatedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentComments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentComments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_CreatedByServiceAccountId",
                table: "DocumentComments",
                column: "CreatedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_CreatedByUserId",
                table: "DocumentComments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_DocumentId",
                table: "DocumentComments",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_ParentCommentId",
                table: "DocumentComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_TenantId_DocumentId_CreatedAt_Id",
                table: "DocumentComments",
                columns: new[] { "TenantId", "DocumentId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentComments");
        }
    }
}
