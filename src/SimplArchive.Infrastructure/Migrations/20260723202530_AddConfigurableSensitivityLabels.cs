using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurableSensitivityLabels : Migration
    {
        // The four defaults, matching the ranks/colours/watermark of the pre-configurable fixed enum (ADR 0399).
        private const string SeedValues =
            "(VALUES ('Public',1,'#2e7d32',false),('Internal',2,'#1565c0',false),('Confidential',3,'#ef6c00',true),('Restricted',4,'#c62828',true)) AS v(name,rank,color,watermark)";

        private static bool IsPostgres(MigrationBuilder b) =>
            b.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The per-tenant label table.
            migrationBuilder.CreateTable(
                name: "SensitivityLabelDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    Watermark = table.Column<bool>(type: "boolean", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensitivityLabelDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SensitivityLabelDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SensitivityLabelDefinitions_TenantId_Name",
                table: "SensitivityLabelDefinitions",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // 2. Seed the four defaults per existing tenant (Postgres only; SQLite tests build the current model
            //    directly via EnsureCreated and never run migrations).
            if (IsPostgres(migrationBuilder))
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "SensitivityLabelDefinitions" ("Id", "TenantId", "Name", "Rank", "Color", "Watermark", "CreatedAt")
                    SELECT gen_random_uuid(), t."Id", v.name, v.rank, v.color, v.watermark, now()
                    FROM "Tenants" t CROSS JOIN {SeedValues};
                    """);
            }

            // 3. Documents.SensitivityLabelId.
            migrationBuilder.AddColumn<Guid>(
                name: "SensitivityLabelId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            // 4. Backfill by Rank = the old int value (Public=1..Restricted=4; 0=None → null).
            if (IsPostgres(migrationBuilder))
            {
                migrationBuilder.Sql("""
                    UPDATE "Documents" d SET "SensitivityLabelId" = l."Id"
                    FROM "SensitivityLabelDefinitions" l
                    WHERE l."TenantId" = d."TenantId" AND l."Rank" = d."SensitivityLabel" AND d."SensitivityLabel" > 0;
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SensitivityLabelId",
                table: "Documents",
                column: "SensitivityLabelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_SensitivityLabelDefinitions_SensitivityLabelId",
                table: "Documents",
                column: "SensitivityLabelId",
                principalTable: "SensitivityLabelDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // 5. Drop the old int (data preserved in the FK above; allowlisted in MigrationDataPreservationTests).
            migrationBuilder.DropColumn(
                name: "SensitivityLabel",
                table: "Documents");

            // 6. MaskVersions.DefaultSensitivityLabelId (no backfill — masks had no default before).
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultSensitivityLabelId",
                table: "MaskVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaskVersions_DefaultSensitivityLabelId",
                table: "MaskVersions",
                column: "DefaultSensitivityLabelId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaskVersions_SensitivityLabelDefinitions_DefaultSensitivity~",
                table: "MaskVersions",
                column: "DefaultSensitivityLabelId",
                principalTable: "SensitivityLabelDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-add the int, backfill it from the label's Rank, then drop the configurable model.
            migrationBuilder.AddColumn<int>(
                name: "SensitivityLabel",
                table: "Documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            if (IsPostgres(migrationBuilder))
            {
                migrationBuilder.Sql("""
                    UPDATE "Documents" d SET "SensitivityLabel" = l."Rank"
                    FROM "SensitivityLabelDefinitions" l
                    WHERE l."Id" = d."SensitivityLabelId";
                    """);
            }

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_SensitivityLabelDefinitions_SensitivityLabelId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_MaskVersions_SensitivityLabelDefinitions_DefaultSensitivity~",
                table: "MaskVersions");

            migrationBuilder.DropIndex(
                name: "IX_MaskVersions_DefaultSensitivityLabelId",
                table: "MaskVersions");

            migrationBuilder.DropIndex(
                name: "IX_Documents_SensitivityLabelId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "DefaultSensitivityLabelId",
                table: "MaskVersions");

            migrationBuilder.DropColumn(
                name: "SensitivityLabelId",
                table: "Documents");

            migrationBuilder.DropTable(
                name: "SensitivityLabelDefinitions");
        }
    }
}
