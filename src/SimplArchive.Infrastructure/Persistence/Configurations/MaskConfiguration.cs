using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class MaskConfiguration : IEntityTypeConfiguration<Mask>
{
    public void Configure(EntityTypeBuilder<Mask> builder)
    {
        // Composite (TenantId, Id), not a bare Id — see ADR "Mask composite primary key for cross-tenant
        // well-known IDs". Lets every tenant have its own "Folder"/"Basic Entry"/"eMail" mask sharing the
        // exact same 3 well-known Id values, since a bare Id PK would make that a cross-tenant collision.
        builder.HasKey(m => new { m.TenantId, m.Id });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
