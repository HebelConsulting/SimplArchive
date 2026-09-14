using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConcurrencyTokensForTopMutableEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "WorkflowStates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "Tenants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "ServiceAccounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "AclEntries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // A DISTINCT token per existing row. AddColumn can only give them all ONE value, and a token every
            // row shares is no token at all: an If-Match carrying the all-zeros guid would match every row that
            // had not yet been written since the upgrade — precisely the lost update this column exists to
            // stop, for exactly as long as a row stays untouched. Purely additive: the column is new, so
            // nothing is overwritten. SaveChanges regenerates the value on every write from here on.
            foreach (var table in new[] { "WorkflowStates", "Users", "Tenants", "ServiceAccounts", "AclEntries" })
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"ConcurrencyToken\" = gen_random_uuid();");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "WorkflowStates");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "AclEntries");
        }
    }
}
