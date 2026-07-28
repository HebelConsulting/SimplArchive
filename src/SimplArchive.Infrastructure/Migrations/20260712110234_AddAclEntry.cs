using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAclEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AclEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanSee = table.Column<bool>(type: "boolean", nullable: false),
                    CanReadContent = table.Column<bool>(type: "boolean", nullable: false),
                    CanEditContent = table.Column<bool>(type: "boolean", nullable: false),
                    CanEditIndexData = table.Column<bool>(type: "boolean", nullable: false),
                    CanDelete = table.Column<bool>(type: "boolean", nullable: false),
                    CanCreateSubItems = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AclEntries", x => x.Id);
                    table.CheckConstraint("CK_AclEntries_ExactlyOnePrincipal", "(\"UserId\" IS NOT NULL AND \"GroupId\" IS NULL) OR (\"UserId\" IS NULL AND \"GroupId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AclEntries_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AclEntries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AclEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AclEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_GroupId",
                table: "AclEntries",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_GroupId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_RepositoryId_UserId",
                table: "AclEntries",
                columns: new[] { "RepositoryId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_TenantId",
                table: "AclEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_UserId",
                table: "AclEntries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AclEntries");
        }
    }
}
