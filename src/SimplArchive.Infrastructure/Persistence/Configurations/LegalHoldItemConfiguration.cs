using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.LegalHolds;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Legal hold & retention enforcement". The LegalHold FK cascades (removing a hold takes its items);
// the Document FK is Restrict (a held document can't be hard-deleted anyway). Unique per (hold, document) so a
// document can't be added to the same hold twice.
public class LegalHoldItemConfiguration : IEntityTypeConfiguration<LegalHoldItem>
{
    public void Configure(EntityTypeBuilder<LegalHoldItem> builder)
    {
        builder.HasKey(i => i.Id);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<LegalHold>()
            .WithMany()
            .HasForeignKey(i => i.LegalHoldId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(i => i.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.TenantId, i.LegalHoldId, i.DocumentId }).IsUnique();
    }
}
