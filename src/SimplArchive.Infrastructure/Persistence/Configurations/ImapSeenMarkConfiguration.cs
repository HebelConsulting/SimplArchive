using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Imap;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "IMAP endpoint: persisted read state". Composite (UserId, DocumentId) key — the row IS the \Seen
// flag; the (TenantId, DocumentId) index backs the per-mailbox unseen-count query. Cascade from both ends:
// a purged document or a deleted user takes its marks along.
public class ImapSeenMarkConfiguration : IEntityTypeConfiguration<ImapSeenMark>
{
    public void Configure(EntityTypeBuilder<ImapSeenMark> builder)
    {
        builder.HasKey(m => new { m.UserId, m.DocumentId });

        builder.HasIndex(m => new { m.TenantId, m.DocumentId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(m => m.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
