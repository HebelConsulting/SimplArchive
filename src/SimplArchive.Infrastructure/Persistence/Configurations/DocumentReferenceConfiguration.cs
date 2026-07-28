using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class DocumentReferenceConfiguration : IEntityTypeConfiguration<DocumentReference>
{
    public void Configure(EntityTypeBuilder<DocumentReference> builder)
    {
        builder.HasKey(r => r.Id);

        // No duplicate shortcut of the same target in the same folder. Plain unique index (all columns
        // non-null, no NULL-parent complication). The (TenantId, ParentFolderId) prefix also serves the
        // per-folder listing query.
        builder.HasIndex(r => new { r.TenantId, r.ParentFolderId, r.TargetDocumentId }).IsUnique();

        // Can't reference an item into itself (a folder shortcut inside the same folder). A cheap portable
        // DB-level backstop; the fuller no-cycle rule (can't reference a folder into its own subtree) lives
        // in the reference-creation endpoint, its only writer. See ADR "Desktop drag-and-drop move and
        // reference".
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_DocumentReferences_NotSelf",
            "\"TargetDocumentId\" <> \"ParentFolderId\""));

        // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set — same CASE WHEN "exactly one"
        // shape as DocumentVersion/DocumentComment.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_DocumentReferences_ExactlyOneCreator",
            "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // A hard-deleted document takes its shortcuts with it — as the containing folder and as the target.
        // (Normal deletes are soft — DeletedAt — and handled by query filtering, not this cascade.)
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(r => r.ParentFolderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(r => r.TargetDocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(r => r.CreatedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
