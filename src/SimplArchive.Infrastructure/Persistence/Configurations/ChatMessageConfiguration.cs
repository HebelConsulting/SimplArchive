using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Body).IsRequired();

        // Lists a document's thread in order (TenantId leads for tenant-scoped locality).
        builder.HasIndex(c => new { c.TenantId, c.DocumentId, c.CreatedAt, c.Id });

        // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set — same CASE WHEN "exactly one"
        // shape as DocumentVersion's creator check (ADR "Document version upload/download endpoints").
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ChatMessages_ExactlyOneCreator",
                "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
                "CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            // Kind and DocumentVersionId are two halves of one fact (ADR 0545): a UserPost (0) is about the
            // document, so it names no version; VersionFiled (1) and VersionActivated (2) are about a specific
            // version and cannot render their "Version N" label — or, for VersionFiled, even pick their
            // sentence — without one. Pairing them here means a system entry can never exist that the clients
            // are unable to draw.
            //
            // AttachmentRefused (3) is the third shape and joins UserPost in naming no version (ADR 0718): it
            // records an attachment that was refused, so there is no version for it to point at. The original
            // constraint deliberately left no value for a fourth kind, which is exactly why widening it is a
            // migration rather than an enum edit — the guard did its job.
            t.HasCheckConstraint(
                "CK_ChatMessages_KindVersionPairing",
                "(\"Kind\" = 0 AND \"DocumentVersionId\" IS NULL) OR " +
                "(\"Kind\" IN (1, 2) AND \"DocumentVersionId\" IS NOT NULL) OR " +
                "(\"Kind\" = 3 AND \"DocumentVersionId\" IS NULL)");
        });

        // Restrict, matching how annotations anchor a version: the document-delete cascade already removes the
        // whole thread, and a version is never deleted on its own.
        builder.HasOne<DocumentVersion>()
            .WithMany()
            .HasForeignKey(c => c.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a document deletes its comment thread, same as its versions/field values.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing reply link — Restrict (not Cascade): a document delete already removes the whole
        // thread via the DocumentId cascade above, so a second cascade path isn't needed, and comments are
        // append-only so a parent is never deleted on its own.
        builder.HasOne<ChatMessage>()
            .WithMany()
            .HasForeignKey(c => c.ParentMessageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(c => c.CreatedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
