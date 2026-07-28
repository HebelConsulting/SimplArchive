using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTagCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RestrictTagsToCatalog",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TagDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TagDefinitions_TenantId_Name",
                table: "TagDefinitions",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // Backfill the catalog from existing free-form tags so it reflects current usage (ADR "Tag controlled
            // vocabulary"). Postgres-only — migrations execute against Postgres; the SQLite tests build the schema
            // via EnsureCreated and never run this migration.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    INSERT INTO "TagDefinitions" ("Id", "TenantId", "Name", "CreatedAt")
                    SELECT gen_random_uuid(), "TenantId", "Tag", now()
                    FROM (SELECT DISTINCT "TenantId", "Tag" FROM "DocumentTags") AS distinct_tags;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TagDefinitions");

            migrationBuilder.DropColumn(
                name: "RestrictTagsToCatalog",
                table: "Tenants");
        }
    }
}
