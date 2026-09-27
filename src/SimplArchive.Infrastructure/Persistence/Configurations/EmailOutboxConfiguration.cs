using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Notifications;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR 0840. The queue of notifications owed an email — drained oldest-first, one claim per row.
public class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutbox>
{
    public void Configure(EntityTypeBuilder<EmailOutbox> builder)
    {
        builder.HasKey(o => o.Id);

        // ONE row per notification. This makes a duplicate ENQUEUE impossible — which is worth stating plainly,
        // because it is NOT what stops a duplicate SEND: two sweeps can still read one row. The claim below does
        // that. What this buys is that the enqueue in SaveChanges cannot double-insert if it is ever called twice
        // for one notification.
        builder.HasIndex(o => o.NotificationId).IsUnique();

        // The sweep's exact predicate: State == Pending, oldest first. Without it this is a scan of a table that
        // is empty in the steady state but grows under a mail outage — which is precisely when the sweep is hot.
        builder.HasIndex(o => new { o.State, o.EnqueuedAt, o.Id });

        // NO store default on State. A HasDefaultValue here would reintroduce the trap EmailOutboxState avoids by
        // starting at 1: a store default makes the CLR default unstorable, and every row is written by the
        // enqueue with an explicit state anyway.
        builder.Property(o => o.Attempts).HasDefaultValue(0);

        // CASCADE, deliberately. A notification that is hard-deleted owes no email, so its queue row must go with
        // it rather than becoming a row pointing at nothing — and unlike SearchIndexOutbox there is no
        // delete-from-index work that has to outlive the entity, so nothing is lost by cascading.
        builder.HasOne<Notification>()
            .WithMany()
            .HasForeignKey(o => o.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        // No tenant FK and no ITenantScoped filter: the sweep reads across every tenant and sets the tenant
        // context per row — see EmailOutbox, and SearchIndexOutbox before it.
    }
}
