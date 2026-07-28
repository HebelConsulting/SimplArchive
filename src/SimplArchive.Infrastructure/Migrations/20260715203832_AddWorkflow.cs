using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowStates_DocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowStates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowStates_Users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowStateId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: false),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PerformedByServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTransitions", x => x.Id);
                    table.CheckConstraint("CK_WorkflowTransitions_ExactlyOnePerformer", "(CASE WHEN \"PerformedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"PerformedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_WorkflowTransitions_RejectionReason", "(\"ToStatus\" = 3 AND \"RejectionReason\" IS NOT NULL) OR (\"ToStatus\" <> 3 AND \"RejectionReason\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_ServiceAccounts_PerformedByServiceAccou~",
                        column: x => x.PerformedByServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_Users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_Users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_WorkflowStates_WorkflowStateId",
                        column: x => x.WorkflowStateId,
                        principalTable: "WorkflowStates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStates_AssignedToUserId",
                table: "WorkflowStates",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStates_DocumentVersionId",
                table: "WorkflowStates",
                column: "DocumentVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStates_TenantId_AssignedToUserId_Status",
                table: "WorkflowStates",
                columns: new[] { "TenantId", "AssignedToUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_AssignedToUserId",
                table: "WorkflowTransitions",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_PerformedByServiceAccountId",
                table: "WorkflowTransitions",
                column: "PerformedByServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_PerformedByUserId",
                table: "WorkflowTransitions",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_TenantId_WorkflowStateId_CreatedAt_Id",
                table: "WorkflowTransitions",
                columns: new[] { "TenantId", "WorkflowStateId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_WorkflowStateId",
                table: "WorkflowTransitions",
                column: "WorkflowStateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowTransitions");

            migrationBuilder.DropTable(
                name: "WorkflowStates");
        }
    }
}
