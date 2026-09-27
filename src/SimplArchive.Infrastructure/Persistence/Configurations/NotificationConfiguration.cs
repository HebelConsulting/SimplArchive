using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Notifications (in-app, first slice)". Per-User in-app notifications; the
// (TenantId, RecipientUserId, CreatedAt, Id) index backs the recipient's newest-first intray + unread count.
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Title).IsRequired().HasMaxLength(200);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(1000);
        // Coalesced-event count (ADR "Notification digest / coalescing") — default 1 so existing rows + normal
        // notifications carry 1.
        builder.Property(n => n.EventCount).HasDefaultValue(1);

        // NO index on (EmailedAt, EmailFailedAt, Id) any more, and no EmailAttempts column (ADR 0840). That index
        // backed the old pending-email predicate, which was a scan of the two timestamps; the pending set now
        // lives in EmailOutbox with its own (State, EnqueuedAt, Id) index, so this one served nothing but its own
        // maintenance cost on a table that only grows. The timestamps themselves stay as the RECORD of what
        // happened — nothing queries them.
        builder.HasIndex(n => new { n.TenantId, n.RecipientUserId, n.CreatedAt, n.Id });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(n => n.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(n => n.RecipientUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The related document is optional; if a document is ever hard-deleted the link nulls rather than
        // blocking (today's delete is soft, so this never actually fires).
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(n => n.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
