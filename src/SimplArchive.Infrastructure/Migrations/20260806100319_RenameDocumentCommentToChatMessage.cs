using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameDocumentCommentToChatMessage : Migration
    {
        // HAND-WRITTEN. EF scaffolded this as DropTable + CreateTable, which would have destroyed every existing
        // chat message: the model diff sees one entity disappear and another appear, and cannot infer a rename.
        // (MigrationDataPreservationTests would have caught it — a DropTable is exactly what it guards.)
        //
        // A rename is a metadata-only operation, so this preserves every row. Postgres keeps the ORIGINAL names of
        // indexes and constraints when a table is renamed, so each is renamed explicitly too — otherwise the
        // database keeps "PK_DocumentComments"/"FK_DocumentComments_*" while the model snapshot expects the new
        // names, and the next migration that touches one of them fails against a name that isn't there.
        //
        // Raw SQL for the constraints only: MigrationBuilder has RenameTable/RenameColumn/RenameIndex but no
        // rename for a primary key, foreign key or check constraint. Migrations run against PostgreSQL only —
        // the SQLite tests build their schema from the model via EnsureCreated, never from migrations.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "DocumentComments", newName: "ChatMessages");
            migrationBuilder.RenameColumn(table: "ChatMessages", name: "ParentCommentId", newName: "ParentMessageId");

            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_DocumentComments_CreatedByServiceAccountId", newName: "IX_ChatMessages_CreatedByServiceAccountId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_DocumentComments_CreatedByUserId", newName: "IX_ChatMessages_CreatedByUserId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_DocumentComments_DocumentId", newName: "IX_ChatMessages_DocumentId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_DocumentComments_ParentCommentId", newName: "IX_ChatMessages_ParentMessageId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_DocumentComments_TenantId_DocumentId_CreatedAt_Id", newName: "IX_ChatMessages_TenantId_DocumentId_CreatedAt_Id");

            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"PK_DocumentComments\" TO \"PK_ChatMessages\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"CK_DocumentComments_ExactlyOneCreator\" TO \"CK_ChatMessages_ExactlyOneCreator\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_DocumentComments_DocumentComments_ParentCommentId\" TO \"FK_ChatMessages_ChatMessages_ParentMessageId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_DocumentComments_Documents_DocumentId\" TO \"FK_ChatMessages_Documents_DocumentId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_DocumentComments_ServiceAccounts_CreatedByServiceAccountId\" TO \"FK_ChatMessages_ServiceAccounts_CreatedByServiceAccountId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_DocumentComments_Tenants_TenantId\" TO \"FK_ChatMessages_Tenants_TenantId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_DocumentComments_Users_CreatedByUserId\" TO \"FK_ChatMessages_Users_CreatedByUserId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_ChatMessages_Users_CreatedByUserId\" TO \"FK_DocumentComments_Users_CreatedByUserId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_ChatMessages_Tenants_TenantId\" TO \"FK_DocumentComments_Tenants_TenantId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_ChatMessages_ServiceAccounts_CreatedByServiceAccountId\" TO \"FK_DocumentComments_ServiceAccounts_CreatedByServiceAccountId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_ChatMessages_Documents_DocumentId\" TO \"FK_DocumentComments_Documents_DocumentId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"FK_ChatMessages_ChatMessages_ParentMessageId\" TO \"FK_DocumentComments_DocumentComments_ParentCommentId\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"CK_ChatMessages_ExactlyOneCreator\" TO \"CK_DocumentComments_ExactlyOneCreator\";");
            migrationBuilder.Sql("ALTER TABLE \"ChatMessages\" RENAME CONSTRAINT \"PK_ChatMessages\" TO \"PK_DocumentComments\";");

            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_ChatMessages_TenantId_DocumentId_CreatedAt_Id", newName: "IX_DocumentComments_TenantId_DocumentId_CreatedAt_Id");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_ChatMessages_ParentMessageId", newName: "IX_DocumentComments_ParentCommentId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_ChatMessages_DocumentId", newName: "IX_DocumentComments_DocumentId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_ChatMessages_CreatedByUserId", newName: "IX_DocumentComments_CreatedByUserId");
            migrationBuilder.RenameIndex(table: "ChatMessages", name: "IX_ChatMessages_CreatedByServiceAccountId", newName: "IX_DocumentComments_CreatedByServiceAccountId");

            migrationBuilder.RenameColumn(table: "ChatMessages", name: "ParentMessageId", newName: "ParentCommentId");
            migrationBuilder.RenameTable(name: "ChatMessages", newName: "DocumentComments");
        }
    }
}
