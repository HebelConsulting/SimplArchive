// Sends the WebDAV-Push notification when a collection changes (#564 slice 3, ADR 0622). Best-effort by
// design: a push is an OPTIMISATION over polling, so a failed send must never fail the write that triggered
// it — the client still discovers the change on its next poll, just later.
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.CalDav;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Api.CalDav.Xml;
using WebPush;

namespace SimplArchive.Api.CalDav;

public sealed class DavPushNotifier
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DavPushConfiguration _push;
    private readonly ILogger<DavPushNotifier> _logger;

    public DavPushNotifier(SimplArchiveDbContext dbContext, DavPushConfiguration push, ILogger<DavPushNotifier> logger)
    {
        _dbContext = dbContext;
        _push = push;
        _logger = logger;
    }

    /// <summary>
    /// Tells every live subscriber that this collection changed. The payload carries only the collection's
    /// topic and new sync-token — never item content: a push service is a third party, and the client can
    /// fetch what changed over the authenticated channel it already has.
    /// </summary>
    public async Task NotifyAsync(Guid folderId, long syncSequence, CancellationToken cancellationToken)
    {
        if (!_push.IsEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var subscriptions = await _dbContext.DavPushSubscriptions
            .Where(s => s.FolderId == folderId && (s.ExpiresAt == null || s.ExpiresAt > now))
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = $"""<?xml version="1.0" encoding="utf-8"?><P:push-message xmlns:D="DAV:" xmlns:P="{DavNames.Push.NamespaceName}"><D:propstat><D:prop><D:sync-token>{DavTokens.Format(syncSequence)}</D:prop></D:propstat></P:push-message>""";
        var client = new WebPushClient();
        var vapid = new VapidDetails(_push.Subject, _push.VapidPublicKey, _push.VapidPrivateKey);
        var gone = new List<DavPushSubscription>();

        foreach (var subscription in subscriptions)
        {
            try
            {
                await client.SendNotificationAsync(
                    new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth), payload, vapid, cancellationToken);
            }
            catch (WebPushException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // The endpoint is dead — the device uninstalled or reset. Pruning it is the point of the
                // distinction: retrying a Gone endpoint forever is how a push queue silently rots.
                gone.Add(subscription);
            }
            catch (Exception e)
            {
                // NEVER the endpoint URL: it identifies a device.
                _logger.LogWarning(e, "WebDAV-Push delivery failed for subscription {SubscriptionId}", subscription.Id);
            }
        }

        if (gone.Count > 0)
        {
            _dbContext.DavPushSubscriptions.RemoveRange(gone);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Pruned {Count} dead WebDAV-Push subscription(s)", gone.Count);
        }
    }
}
