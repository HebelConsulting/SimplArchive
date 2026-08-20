using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class MaskFileExtensionConfiguration : IEntityTypeConfiguration<MaskFileExtension>
{
    public void Configure(EntityTypeBuilder<MaskFileExtension> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Extension).IsRequired().HasMaxLength(32);

        // At most ONE mask per extension per tenant. The constraint is the design: it makes the picker's answer
        // and the classifier's answer necessarily the same, and removes the "which mask wins for .pdf" question
        // by making the ambiguity unrepresentable rather than arbitrated at read time.
        builder.HasIndex(e => new { e.TenantId, e.Extension }).IsUnique();

        // The FK is composite because Mask's key is (TenantId, Id) — a bare MaskId would not identify a row.
        // Cascade: an extension has no meaning without the mask that claims it.
        builder.HasOne<Mask>()
            .WithMany(m => m.FileExtensions)
            .HasForeignKey(e => new { e.TenantId, e.MaskId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
