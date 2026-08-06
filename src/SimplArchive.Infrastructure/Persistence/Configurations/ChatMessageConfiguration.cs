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
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ChatMessages_ExactlyOneCreator",
            "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

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
