using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(200);

        // Per-folder default contents sort order (ADR "Per-folder contents sort order"). Store default
        // DocumentDate so the migration backfills existing folders to it; new documents get it from the entity
        // default. Only meaningful for a folder.
        builder.Property(d => d.ContentsSortOrder).HasDefaultValue(FolderContentsSortOrder.DocumentDate);

        // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set — see ADR "Repositories
        // controller and Document creation". Same CASE WHEN "exactly one" shape already used for
        // AclEntry's principal/scope columns and DocumentVersion's own creator columns.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Documents_ExactlyOneCreator",
            "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(d => d.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MaskVersion>()
            .WithMany()
            .HasForeignKey(d => d.MaskVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        // The configurable sensitivity label (ADR "Configurable sensitivity labels + upload defaults"); null =
        // None. Restrict — a label in use can't be hard-deleted (retire it instead).
        builder.HasOne<SensitivityLabelDefinition>()
            .WithMany()
            .HasForeignKey(d => d.SensitivityLabelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(d => d.CreatedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Check-out (exclusive edit lock) — see ADR "Document check-out / check-in". The holder is a User;
        // Restrict so a User with an active checkout can't be hard-deleted out from under the lock.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.CheckedOutByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // CheckedOutByUserId/CheckedOutAt are set/cleared together — same defense-in-depth pairing as
        // Tenant.Status/DeactivatedAt (ADR "Status/DeactivatedAt consistency").
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Documents_Checkout_Consistency",
            "(\"CheckedOutByUserId\" IS NULL AND \"CheckedOutAt\" IS NULL) OR " +
            "(\"CheckedOutByUserId\" IS NOT NULL AND \"CheckedOutAt\" IS NOT NULL)"));

        // Backs the "my check-outs" tab query (documents this user currently holds).
        builder.HasIndex(d => new { d.TenantId, d.CheckedOutByUserId });

        // Import provenance (ADR "Idempotent re-import"): one imported copy per (origin tenant, origin document)
        // per target tenant — the partial unique index also serves as the re-import match lookup. NULL !=  NULL,
        // so natively-created rows (OriginDocumentId IS NULL) are exempt (the same reasoning as AclEntry's
        // per-principal partial indexes).
        builder.HasIndex(d => new { d.TenantId, d.OriginTenantId, d.OriginDocumentId })
            .IsUnique()
            .HasFilter("\"OriginDocumentId\" IS NOT NULL");

        // Personal repository (ADR "Per-user personal repository"): a root Document flagged as a user's private
        // space. Restrict so a User with a personal repository isn't hard-deleted out from under it.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.PersonalOfUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // At most one personal repository per user (NULL != NULL exempts ordinary repositories).
        builder.HasIndex(d => new { d.TenantId, d.PersonalOfUserId })
            .IsUnique()
            .HasFilter("\"PersonalOfUserId\" IS NOT NULL");
    }
}
