using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Notifications;

// Writes an in-app notification for a recipient User (ADR "Notifications (in-app, first slice)"). Registered
// scoped in AddInfrastructure. Best-effort in its own commit (after the triggering action has committed), so a
// failure here doesn't break the action; no-ops when no tenant is set, and never notifies the actor about
// their own action.
public sealed class NotificationService : INotificationService
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public NotificationService(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentUserAccessor = currentUserAccessor;
    }

    public async Task NotifyAsync(
        Guid recipientUserId,
        NotificationType type,
        string title,
        string body,
        Guid? documentId = null,
        CancellationToken cancellationToken = default)
    {
        if (_currentTenantAccessor.TenantId is not { } tenantId)
        {
            return;
        }

        // Don't notify the actor about their own action (a ServiceAccount actor has no UserId, so it never
        // matches — its notifications always go to a different User).
        if (_currentUserAccessor.UserId == recipientUserId)
        {
            return;
        }

        await AddOrCoalesceAsync(tenantId, recipientUserId, type, title, body, documentId, DateTimeOffset.UtcNow, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // Notification digest / coalescing (ADR "Notification digest / coalescing"): a burst of activity on one
    // document is collapsed into a single growing notification. The two high-volume routine types
    // (folder/subtree follow activity + comments) coalesce; discrete actionable events (workflow / access /
    // reminders / check-out + quota warnings) stay one-per-event.
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromHours(6);

    private static bool IsCoalescable(NotificationType type) =>
        type is NotificationType.SubscribedActivity or NotificationType.CommentPosted;

    // Adds a new notification, or — for a coalescable type on a document — merges the event into the recipient's
    // existing UNREAD notification for that same document within the coalesce window (incrementing EventCount and
    // refreshing the title/body/timestamp) instead of inserting a new row. Does not save (the caller does).
    // Leaves EmailedAt untouched: an un-emailed digest is emailed once with its final count; an already-emailed one
    // isn't re-emailed on each subsequent event.
    private async Task AddOrCoalesceAsync(
        Guid tenantId, Guid recipientUserId, NotificationType type, string title, string body, Guid? documentId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (documentId is { } docId && IsCoalescable(type))
        {
            // Ordered/filtered client-side — SQLite (the integration tests' provider) can't translate a
            // DateTimeOffset ORDER BY / comparison; the unread set for one (recipient, document, type) is tiny.
            var unread = await _dbContext.Notifications
                .Where(n => n.RecipientUserId == recipientUserId && n.DocumentId == docId && n.Type == type && n.ReadAt == null)
                .ToListAsync(cancellationToken);
            var latest = unread.OrderByDescending(n => n.CreatedAt).FirstOrDefault();
            if (latest is not null && now - latest.CreatedAt <= CoalesceWindow)
            {
                latest.EventCount += 1;
                latest.Title = title;
                latest.Body = body;
                latest.CreatedAt = now;
                return;
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RecipientUserId = recipientUserId,
            Type = type,
            Title = title,
            Body = body,
            DocumentId = documentId,
            CreatedAt = now,
            EventCount = 1,
        });
    }

    public async Task NotifyDocumentSubscribersAsync(
        Guid documentId,
        NotificationType type,
        string title,
        string body,
        IEnumerable<Guid>? excludeUserIds = null,
        CancellationToken cancellationToken = default)
    {
        if (_currentTenantAccessor.TenantId is not { } tenantId)
        {
            return;
        }

        // Never notify the actor about their own action, nor anyone the primary trigger already notified.
        var exclude = excludeUserIds is null ? new HashSet<Guid>() : [.. excludeUserIds];
        if (_currentUserAccessor.UserId is { } actorId)
        {
            exclude.Add(actorId);
        }

        // Folder / subtree subscriptions (ADR "Folder / subtree subscriptions"): notify not just followers of the
        // changed document, but followers of any ANCESTOR folder too — so following a folder means "notify me of
        // any change within its subtree". Walk up the parent chain and collect the whole scope.
        var scopeIds = new List<Guid> { documentId };
        var currentId = documentId;
        while (await _dbContext.Documents.Where(d => d.Id == currentId).Select(d => d.ParentId).FirstOrDefaultAsync(cancellationToken) is { } parentId)
        {
            scopeIds.Add(parentId);
            currentId = parentId;
        }

        var subscriberIds = await _dbContext.DocumentSubscriptions
            .Where(s => scopeIds.Contains(s.DocumentId))
            .Select(s => s.UserId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var any = false;
        foreach (var userId in subscriberIds.Distinct().Where(id => !exclude.Contains(id)))
        {
            await AddOrCoalesceAsync(tenantId, userId, type, title, body, documentId, now, cancellationToken);
            any = true;
        }

        if (any)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
