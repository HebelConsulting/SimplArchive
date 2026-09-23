using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// #1335 / ADR 0820. One ACTIVE ingest keypair per tenant — the unique TenantId index is the invariant, so
// a rotation is a replace, never a quiet second key beside the first.
public class TenantIngestKeyConfiguration : IEntityTypeConfiguration<TenantIngestKey>
{
    public void Configure(EntityTypeBuilder<TenantIngestKey> builder)
    {
        builder.HasKey(k => k.Id);

        builder.Property(k => k.CertificatePem).IsRequired();
        builder.Property(k => k.PrivateKeyProtected).IsRequired();
        builder.Property(k => k.IngestAddress).IsRequired().HasMaxLength(320);

        builder.HasIndex(k => k.TenantId).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(k => k.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
