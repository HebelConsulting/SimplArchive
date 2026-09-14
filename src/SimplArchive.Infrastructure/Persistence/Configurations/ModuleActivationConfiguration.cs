using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ModuleActivationConfiguration : IEntityTypeConfiguration<ModuleActivation>
{
    public void Configure(EntityTypeBuilder<ModuleActivation> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ModuleId)
            .IsRequired()
            .HasMaxLength(128);

        // The verifying key's SHA-256 thumbprint as lowercase hex — 64 chars, fixed by the hash (ADR 0793).
        // NULLABLE on purpose: a row activated before this existed genuinely does not know which key it was,
        // and NULL says that where any default would invent an answer. Deliberately UNINDEXED — one row per
        // (tenant, module), so the compromise question is a scan of a table measured in tens of rows.
        builder.Property(a => a.VerifiedByKeyThumbprint)
            .HasMaxLength(64);

        // One activation per (tenant, module) — renewal UPDATES the row (ADR 0740); the filed license
        // documents and the audit trail are the history.
        builder.HasIndex(a => new { a.TenantId, a.ModuleId }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // LicenseDocumentId is deliberately a PLAIN COLUMN, not a FK — the Document.CurrentVersionId
        // precedent (ADR 0503): the license document lives wherever the administrator filed it, and its
        // deletion must neither cascade into nor be blocked by this row.

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.ActivatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
