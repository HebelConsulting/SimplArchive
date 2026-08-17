using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR 0620. Composite (UserId, DocumentId) key — one override per user per collection, and its absence is
// what makes the collection's own default apply, so a "reset to default" is a plain delete. Cascade from both
// ends: a purged collection or a deleted user takes its overrides along. Same shape as ImapSeenMark.
public class DavCollectionColorConfiguration : IEntityTypeConfiguration<DavCollectionColor>
{
    public void Configure(EntityTypeBuilder<DavCollectionColor> builder)
    {
        builder.HasKey(c => new { c.UserId, c.DocumentId });

        builder.Property(c => c.Color).IsRequired().HasMaxLength(32);

        builder.HasIndex(c => new { c.TenantId, c.DocumentId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
