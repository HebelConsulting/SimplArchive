using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// The realtime half of the context's post-commit announcements (ADR "Real-time notifications"), in its own
/// file for the same reason <see cref="DavChangeRecorder"/> is (#806/#466): the context states WHEN — collect
/// before the save, push after the commit — and the responsibility owns HOW.
/// </summary>
internal static class RealtimeChangePusher
{
    /// <summary>
    /// Snapshot the notifications being inserted BEFORE the save (so the state is still Added). A newly
    /// inserted notification pushes; so does a coalesced one — a Modified row whose EventCount changed (ADR
    /// "Notification digest / coalescing"), so a digest bump refreshes the bell live. A mark-read/email
    /// update (only ReadAt/EmailedAt modified) is deliberately not pushed.
    /// </summary>
    internal static List<(Guid UserId, RealtimeNotification Payload)> Collect(IRealtimeNotifier? notifier, ChangeTracker tracker)
    {
        if (notifier is null)
        {
            return [];
        }

        return tracker.Entries<Domain.Notifications.Notification>()
            .Where(e => e.State == EntityState.Added
                || (e.State == EntityState.Modified && e.Property(n => n.EventCount).IsModified))
            .Select(e => (e.Entity.RecipientUserId, new RealtimeNotification(e.Entity.Title, e.Entity.Body)))
            .ToList();
    }

    /// <summary>Best-effort, post-commit: a push failure must never break the mutation — it is persisted.</summary>
    internal static async Task PushAsync(
        IRealtimeNotifier? notifier, List<(Guid UserId, RealtimeNotification Payload)> pushes, CancellationToken cancellationToken)
    {
        if (notifier is null || pushes.Count == 0)
        {
            return;
        }

        foreach (var (userId, payload) in pushes)
        {
            try
            {
                await notifier.NotifyUserAsync(userId, payload, cancellationToken);
            }
            catch
            {
                // swallow — real-time delivery is best-effort; the notification is already persisted.
            }
        }
    }
}
