using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentAnnotations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageIndex = table.Column<int>(type: "integer", nullable: false),
                    PositionX = table.Column<double>(type: "double precision", nullable: false),
                    PositionY = table.Column<double>(type: "double precision", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Color = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentAnnotations", x => x.Id);
                    table.CheckConstraint("CK_DocumentAnnotations_ExactlyOneCreator", "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_DocumentAnnotations_PageIndex", "\"PageIndex\" >= 0");
                    table.CheckConstraint("CK_DocumentAnnotations_Position", "\"PositionX\" >= 0 AND \"PositionX\" <= 1 AND \"PositionY\" >= 0 AND \"PositionY\" <= 1");
                    table.ForeignKey(
                        name: "FK_DocumentAnnotations_DocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentAnnotations_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentAnnotations_ServiceAccounts_CreatedByServiceAccount~",
                        column: x => x.CreatedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentAnnotations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentAnnotations_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAnnotations_CreatedByServiceAccountId",
                table: "DocumentAnnotations",
                column: "CreatedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAnnotations_CreatedByUserId",
                table: "DocumentAnnotations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAnnotations_DocumentId",
                table: "DocumentAnnotations",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAnnotations_DocumentVersionId",
                table: "DocumentAnnotations",
                column: "DocumentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAnnotations_TenantId_DocumentVersionId_PageIndex",
                table: "DocumentAnnotations",
                columns: new[] { "TenantId", "DocumentVersionId", "PageIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentAnnotations");
        }
    }
}
