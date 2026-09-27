using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ORDER MATTERS, and the scaffolder's order was wrong for this migration: it dropped
            // Notifications.EmailAttempts before the table that inherits its values existed. Creating the queue
            // and backfilling FIRST makes the column drop a move rather than a loss.
            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailOutbox_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_NotificationId",
                table: "EmailOutbox",
                column: "NotificationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_State_EnqueuedAt_Id",
                table: "EmailOutbox",
                columns: new[] { "State", "EnqueuedAt", "Id" });

            // THE BACKFILL, without which every notification already awaiting an email would never be sent one —
            // silently. The enqueue lives in SaveChanges and therefore only fires for notifications inserted from
            // now on; the ones already queued by the OLD mechanism (both timestamps null) exist only as rows that
            // nothing would ever look at again. A grow-later mechanism that strands the existing population is a
            // mistake this repository has already paid for once.
            //
            // EnqueuedAt takes the notification's CreatedAt, so backfilled rows drain oldest-first alongside new
            // ones instead of all claiming the same instant. Attempts CARRIES OVER, which is what makes the
            // column drop below a move: a notification three failures into its budget keeps those three rather
            // than being handed a fresh five.
            //
            // Postgres-only, like the other data migrations here: migrations are never executed against SQLite
            // (the integration suite builds its schema with EnsureCreated), and MigrationDataPreservationTests
            // inspects the operations rather than running them.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql(
                    """
                    INSERT INTO "EmailOutbox" ("Id", "NotificationId", "TenantId", "EnqueuedAt", "State", "ClaimedAt", "Attempts")
                    SELECT gen_random_uuid(), n."Id", n."TenantId", n."CreatedAt", 1, NULL, n."EmailAttempts"
                    FROM "Notifications" n
                    WHERE n."EmailedAt" IS NULL AND n."EmailFailedAt" IS NULL;
                    """);
            }

            migrationBuilder.DropIndex(
                name: "IX_Notifications_EmailedAt_EmailFailedAt_Id",
                table: "Notifications");

            // The values are in the queue by now (above), so this drops a column whose data has moved rather
            // than data itself. Allowlisted in MigrationDataPreservationTests with that reason.
            migrationBuilder.DropColumn(
                name: "EmailAttempts",
                table: "Notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailAttempts",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Carry the counts BACK before the queue goes, so a rollback loses no more than the forward
            // migration did. A row mid-flight (State = Sending) returns as a pending notification, which is the
            // old mechanism's only way to say "not yet emailed" — and is the correct reading: it was not.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql(
                    """
                    UPDATE "Notifications" n
                    SET "EmailAttempts" = o."Attempts"
                    FROM "EmailOutbox" o
                    WHERE o."NotificationId" = n."Id";
                    """);
            }

            migrationBuilder.DropTable(
                name: "EmailOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EmailedAt_EmailFailedAt_Id",
                table: "Notifications",
                columns: new[] { "EmailedAt", "EmailFailedAt", "Id" });
        }
    }
}
