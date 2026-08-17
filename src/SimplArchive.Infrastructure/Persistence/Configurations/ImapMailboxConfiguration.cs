using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Imap;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "IMAP endpoint (read-only, first slice)". One row per mailbox folder; the folder id IS the key
// (a mailbox is a folder Document, ADR 0200). Cascade from the document: a purged folder takes its IMAP
// identity with it — a later folder at the same place is a NEW mailbox with a fresh UIDVALIDITY, which is
// exactly the RFC 3501 contract.
public class ImapMailboxConfiguration : IEntityTypeConfiguration<ImapMailbox>
{
    public void Configure(EntityTypeBuilder<ImapMailbox> builder)
    {
        builder.HasKey(m => m.FolderId);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(m => m.FolderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
