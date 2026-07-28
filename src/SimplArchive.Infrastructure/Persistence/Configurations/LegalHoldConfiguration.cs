using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.LegalHolds;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Legal hold & retention enforcement". Tenant FK Restrict like every entity; the placer/releaser User
// FKs are Restrict (a User is never hard-deleted). Indexed by (TenantId, PlacedAt, Id) for the list query.
public class LegalHoldConfiguration : IEntityTypeConfiguration<LegalHold>
{
    public void Configure(EntityTypeBuilder<LegalHold> builder)
    {
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Name).IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(h => h.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(h => h.PlacedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(h => h.ReleasedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(h => new { h.TenantId, h.PlacedAt, h.Id });
    }
}
