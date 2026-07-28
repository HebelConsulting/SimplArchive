using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-production, non-data-preserving: any existing AuditEvents predate the hash chain, so they
            // have no valid Hash and would all collide on Sequence 0 (the new unique index) / make Verify report
            // the chain broken forever. Clear these throwaway rows so the chain starts clean. See ADR "Audit
            // trail hash chain". (A real deployment would never delete audit history — there is none yet.)
            migrationBuilder.Sql("DELETE FROM \"AuditEvents\";");

            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "AuditEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "AuditEvents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_Sequence",
                table: "AuditEvents",
                columns: new[] { "TenantId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TenantId_Sequence",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "AuditEvents");
        }
    }
}
