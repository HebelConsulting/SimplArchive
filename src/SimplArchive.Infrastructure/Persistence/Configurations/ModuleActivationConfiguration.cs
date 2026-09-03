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
