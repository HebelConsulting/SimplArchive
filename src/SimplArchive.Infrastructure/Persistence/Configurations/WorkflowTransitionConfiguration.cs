using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class WorkflowTransitionConfiguration : IEntityTypeConfiguration<WorkflowTransition>
{
    public void Configure(EntityTypeBuilder<WorkflowTransition> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.RejectionReason).HasMaxLength(2000);

        // History ordering per workflow state.
        builder.HasIndex(t => new { t.TenantId, t.WorkflowStateId, t.CreatedAt, t.Id });

        // A rejection reason is required exactly when the transition lands in Rejected (WorkflowStatus.Rejected
        // = 3), and absent otherwise (ADR "Workflow rejection reason requirement", 0143). Defense-in-depth
        // backstop alongside the handler check — same pattern as the other CHECK constraints.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_WorkflowTransitions_RejectionReason",
            "(\"ToStatus\" = 3 AND \"RejectionReason\" IS NOT NULL) OR " +
            "(\"ToStatus\" <> 3 AND \"RejectionReason\" IS NULL)"));

        // Exactly one performer principal — same CASE WHEN "exactly one" shape as DocumentVersion's creator pair.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_WorkflowTransitions_ExactlyOnePerformer",
            "(CASE WHEN \"PerformedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"PerformedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowState>()
            .WithMany()
            .HasForeignKey(t => t.WorkflowStateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.AssignedToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.PerformedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(t => t.PerformedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
