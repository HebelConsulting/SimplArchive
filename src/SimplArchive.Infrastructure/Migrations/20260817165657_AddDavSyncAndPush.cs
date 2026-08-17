using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDavSyncAndPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DavCollectionChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavCollectionChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavCollectionChanges_Documents_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DavCollectionChanges_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DavPushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Auth = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavPushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavPushSubscriptions_Documents_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DavPushSubscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DavPushSubscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DavCollectionChanges_FolderId_Id",
                table: "DavCollectionChanges",
                columns: new[] { "FolderId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DavCollectionChanges_TenantId",
                table: "DavCollectionChanges",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DavPushSubscriptions_FolderId_Endpoint",
                table: "DavPushSubscriptions",
                columns: new[] { "FolderId", "Endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DavPushSubscriptions_TenantId",
                table: "DavPushSubscriptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DavPushSubscriptions_UserId",
                table: "DavPushSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DavCollectionChanges");

            migrationBuilder.DropTable(
                name: "DavPushSubscriptions");
        }
    }
}
