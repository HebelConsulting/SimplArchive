using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccount>
{
    public void Configure(EntityTypeBuilder<ServiceAccount> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.OpenIddictApplicationClientId).IsRequired();

        // Name uniqueness avoids two indistinguishable service accounts in an admin picker, matching every
        // other named entity's precedent (Tenant, Repository, Group, MaskVersion). ClientId uniqueness
        // reflects it being a 1:1 link to one OAuth client identity — see ADR "ServiceAccount data shape
        // (entities-only slice)".
        builder.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.OpenIddictApplicationClientId }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
