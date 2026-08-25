using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// ADR 0628. A mail domain identifies the tenant an envelope recipient belongs to, so the unique index is
// GLOBAL rather than per-tenant: two tenants claiming one domain is precisely the failure it prevents, and a
// (TenantId, NormalizedDomain) index would allow it while looking careful.
public class TenantMailDomainConfiguration : IEntityTypeConfiguration<TenantMailDomain>
{
    public void Configure(EntityTypeBuilder<TenantMailDomain> builder)
    {
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Domain).IsRequired().HasMaxLength(253);

        // 253 is the DNS maximum for a fully-qualified name, so a longer one is not a domain at all.
        builder.Property(d => d.NormalizedDomain).IsRequired().HasMaxLength(253);

        builder.HasIndex(d => d.NormalizedDomain).IsUnique();

        builder.HasIndex(d => d.TenantId);

        // The challenge value (#667). Nullable: a domain the stack's own configuration declared never had one,
        // because it was never challenged. Bounded because it is a generated token, not free text.
        builder.Property(d => d.VerificationToken).HasMaxLength(128);

        // Not mapped: derived from Domain, and a stored copy would be a second thing to keep in step.
        builder.Ignore(d => d.ChallengeName);

        // Cascade: a deleted tenant's domains go with it. Nothing else may claim a domain while its tenant
        // exists, and nothing should keep claiming one after the tenant is gone.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
