using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class MaskVersionConfiguration : IEntityTypeConfiguration<MaskVersion>
{
    public void Configure(EntityTypeBuilder<MaskVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Name).IsRequired().HasMaxLength(200);

        // Unique among each mask's current version only — see ADR "Mask name uniqueness across
        // versions". A plain index across every version would break immediately, since old versions
        // are kept and version bumps that don't rename reuse the same name.
        builder.HasIndex(v => new { v.TenantId, v.Name })
            .IsUnique()
            .HasFilter("\"IsCurrent\" = true");

        builder.HasIndex(v => new { v.MaskId, v.VersionNumber });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(v => v.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Composite FK (TenantId, MaskId) -> Mask(TenantId, Id) — a bare MaskId is no longer sufficient
        // to identify one Mask row once the same Id can repeat across tenants (ADR "Mask composite
        // primary key for cross-tenant well-known IDs").
        builder.HasOne<Mask>()
            .WithMany()
            .HasForeignKey(v => new { v.TenantId, v.MaskId })
            .OnDelete(DeleteBehavior.Cascade);

        // The upload-time default sensitivity label (ADR "Configurable sensitivity labels + upload defaults").
        // SetNull — if the label is (hard-)deleted the default just clears; retiring is the normal path.
        builder.HasOne<SimplArchive.Domain.Documents.SensitivityLabelDefinition>()
            .WithMany()
            .HasForeignKey(v => v.DefaultSensitivityLabelId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
