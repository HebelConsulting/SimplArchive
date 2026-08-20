using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class MaskAdmittedChildConfiguration : IEntityTypeConfiguration<MaskAdmittedChild>
{
    public void Configure(EntityTypeBuilder<MaskAdmittedChild> builder)
    {
        builder.HasKey(e => e.Id);

        builder.HasIndex(e => new { e.TenantId, e.FolderMaskId, e.ChildMaskId }).IsUnique();

        // A Section admits a Section: FolderMaskId and ChildMaskId may name the SAME mask, so nothing here may
        // assume the two differ. That self-reference is why containment was never a list of pairs (#564).
        builder.HasOne<Mask>()
            .WithMany(m => m.AdmittedChildren)
            .HasForeignKey(e => new { e.TenantId, e.FolderMaskId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Mask>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ChildMaskId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
