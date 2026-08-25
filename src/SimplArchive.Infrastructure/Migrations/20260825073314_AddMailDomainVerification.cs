using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDomainVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCheckedAt",
                table: "TenantMailDomains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationToken",
                table: "TenantMailDomains",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VerifiedAt",
                table: "TenantMailDomains",
                type: "timestamp with time zone",
                nullable: true);

            // Every row that already exists is grandfathered as verified (#667).
            //
            // Delivery now accepts only a verified domain, so leaving these null would silently stop mail for
            // every domain registered before this migration — the upgrade failure that looks like nothing at
            // all until someone asks where their mail went. And they cannot be verified afterwards either:
            // they carry no challenge token, so the only route back would be remove-and-re-add.
            //
            // Grandfathering is the honest reading rather than a shortcut. Until this migration there was no
            // surface that wrote this table at all — the only way a row can exist is that an operator put it
            // there deliberately, out of band, which is the same assertion of ownership that makes a
            // configuration-declared domain verified on arrival (ADR 0692).
            migrationBuilder.Sql(
                """UPDATE "TenantMailDomains" SET "VerifiedAt" = "CreatedAt" WHERE "VerifiedAt" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "TenantMailDomains");

            migrationBuilder.DropColumn(
                name: "VerificationToken",
                table: "TenantMailDomains");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "TenantMailDomains");
        }
    }
}
