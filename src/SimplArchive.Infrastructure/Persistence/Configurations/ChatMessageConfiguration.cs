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

            // Kind and DocumentVersionId are two halves of one fact (ADR 0545): a UserPost (0) or DocumentFiled
            // (1) is about the document, so it names no version; VersionFiled (2) and VersionActivated (3) are
            // about a specific version and cannot render their "Version N" label without one. Pairing them here
            // means a system entry can never exist that the clients are unable to draw.
            t.HasCheckConstraint(
                "CK_ChatMessages_KindVersionPairing",
                "(\"Kind\" IN (0, 1) AND \"DocumentVersionId\" IS NULL) OR " +
                "(\"Kind\" IN (2, 3) AND \"DocumentVersionId\" IS NOT NULL)");
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
