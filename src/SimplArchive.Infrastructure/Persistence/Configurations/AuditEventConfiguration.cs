using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADRs "Audit trail (first slice)" and "Audit trail hash chain". Append-only; the (TenantId, Timestamp,
// Id) index backs the newest-first paginated list. Tenant FK Restrict, like every other entity.
public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ActorName).IsRequired().HasMaxLength(400);
        builder.Property(e => e.Action).IsRequired().HasMaxLength(100);
        builder.Property(e => e.TargetType).HasMaxLength(100);
        builder.Property(e => e.TargetName).HasMaxLength(400);
        builder.Property(e => e.Details).HasMaxLength(2000);
        builder.Property(e => e.Hash).IsRequired().HasMaxLength(64);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TenantId, e.Timestamp, e.Id });

        // Unique per-tenant chain position — one fork-free chain per tenant + the concurrent-append backstop
        // (a race to the same Sequence fails the insert, and the recorder retries). ADR "Audit trail hash chain".
        builder.HasIndex(e => new { e.TenantId, e.Sequence }).IsUnique();
    }
}
