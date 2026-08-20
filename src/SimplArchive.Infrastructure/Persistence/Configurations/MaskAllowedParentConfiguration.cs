using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class MaskAllowedParentConfiguration : IEntityTypeConfiguration<MaskAllowedParent>
{
    public void Configure(EntityTypeBuilder<MaskAllowedParent> builder)
    {
        builder.HasKey(e => e.Id);

        // Declaring the same parent twice says nothing the first row did not, and would make the invariant's
        // "may live in X or X" message read as a bug in the message rather than in the data.
        builder.HasIndex(e => new { e.TenantId, e.MaskId, e.ParentMaskId }).IsUnique();

        // Both FKs are composite because Mask's key is (TenantId, Id) — a bare mask id does not identify a row.
        // Cascade on BOTH sides: a restriction naming a mask that no longer exists is not a restriction, it is a
        // row that would refuse everything and explain nothing.
        builder.HasOne<Mask>()
            .WithMany(m => m.AllowedParents)
            .HasForeignKey(e => new { e.TenantId, e.MaskId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Mask>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ParentMaskId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
