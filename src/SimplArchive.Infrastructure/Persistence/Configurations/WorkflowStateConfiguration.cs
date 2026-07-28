using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class WorkflowStateConfiguration : IEntityTypeConfiguration<WorkflowState>
{
    public void Configure(EntityTypeBuilder<WorkflowState> builder)
    {
        builder.HasKey(w => w.Id);

        // One workflow state per version.
        builder.HasIndex(w => w.DocumentVersionId).IsUnique();

        // Backs the task-inbox query (WorkflowState rows InReview + assigned to the caller).
        builder.HasIndex(w => new { w.TenantId, w.AssignedToUserId, w.Status });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(w => w.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting the version (or its document, which cascades) removes its workflow state.
        builder.HasOne<DocumentVersion>()
            .WithMany()
            .HasForeignKey(w => w.DocumentVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(w => w.AssignedToUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
