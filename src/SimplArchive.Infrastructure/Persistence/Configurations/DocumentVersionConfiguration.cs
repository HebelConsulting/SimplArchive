using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.ObjectKey).IsRequired();
        builder.Property(v => v.Sha256Hash).HasMaxLength(64);

        // Optional per-version OCR-language override (ADR "Per-tenant / per-version OCR languages") — null
        // inherits the tenant default.
        builder.Property(v => v.OcrLanguages).HasMaxLength(200);

        // Optional per-version comment (ADR 0528) — the "why this revision" note.
        builder.Property(v => v.Comment).HasMaxLength(2000);

        builder.HasIndex(v => new { v.DocumentId, v.VersionNumber });

        // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set — see ADR "Document version
        // upload/download endpoints (pragmatic slice)". Same CASE WHEN "exactly one" shape already used
        // for AclEntry's principal/scope columns (ADR "AclEntry ServiceAccount principal", ADR "Document
        // ACL inheritance data shape").
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_DocumentVersions_ExactlyOneCreator",
            "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        // Defense-in-depth backstop keeping Status consistent with VersionNumber/Sha256Hash — same
        // pattern as Tenant/Repository's Status/DeactivatedAt check (ADR "Status/DeactivatedAt consistency
        // for Tenant and Repository"). DocumentVersionStatus.Pending/Confirmed are 0/1.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_DocumentVersions_Status_VersionNumber_Sha256Hash",
            "(\"Status\" = 0 AND \"VersionNumber\" IS NULL AND \"Sha256Hash\" IS NULL) OR " +
            "(\"Status\" = 1 AND \"VersionNumber\" IS NOT NULL AND \"Sha256Hash\" IS NOT NULL)"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(v => v.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(v => v.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(v => v.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(v => v.CreatedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
