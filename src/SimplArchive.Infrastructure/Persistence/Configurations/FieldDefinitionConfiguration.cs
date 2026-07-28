using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class FieldDefinitionConfiguration : IEntityTypeConfiguration<FieldDefinition>
{
    public void Configure(EntityTypeBuilder<FieldDefinition> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(200);
        builder.Property(f => f.FormatPattern).HasMaxLength(500);

        // Unique within the same mask version — see ADR "FieldDefinition name uniqueness"; re-pointed
        // at MaskVersionId per ADR "Mask versioning data shape". A MaskVersion's fields never change
        // after creation, so the plain (non-partial) index still applies unchanged.
        builder.HasIndex(f => new { f.MaskVersionId, f.Name }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MaskVersion>()
            .WithMany()
            .HasForeignKey(f => f.MaskVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
