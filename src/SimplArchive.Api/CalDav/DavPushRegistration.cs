// WebDAV-Push registration (#564 slice 3, ADR 0622), ported from SimplCalCon's WebDavPushController: a client
// POSTs a push-register document to a collection to subscribe an RFC 8030 endpoint, and DELETEs the returned
// registration URL to unsubscribe. This is how DAVx⁵ learns of a change without polling — its endpoint is
// typically an ntfy/UnifiedPush distributor.
using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Domain.CalDav;

namespace SimplArchive.Api.CalDav;

internal static class DavPushRegistration
{
    internal static async Task<IActionResult> RegisterAsync(
        DavControllerContext context, DavPushConfiguration push, Guid folderId)
    {
        // Not-available and not-permitted answer alike: whether a collection exists is not something an
        // unauthorised caller should learn from the push endpoint either.
        if (!push.IsEnabled
            || await DavTree.CollectionAsync(context.Db, context.Rights, context.UserId, context.Protocol, folderId, context.Cancellation) is null)
        {
            return PushNotAvailable();
        }

        var body = await context.ReadBodyAsync();
        if (body is null || body.Name != DavNames.Push + "push-register")
        {
            return new BadRequestResult();
        }

        var subscription = body.Element(DavNames.Push + "subscription")?.Element(DavNames.Push + "web-push-subscription");
        var endpoint = subscription?.Element(DavNames.Push + "push-resource")?.Value.Trim();
        var p256dh = subscription?.Element(DavNames.Push + "subscription-public-key")?.Value.Trim();
        var auth = subscription?.Element(DavNames.Push + "auth-secret")?.Value.Trim();

        if (endpoint is not { Length: > 0 } || p256dh is not { Length: > 0 } || auth is not { Length: > 0 })
        {
            return new BadRequestResult();
        }

        // The server decides the expiry: a client may ask for less than the TTL, never more.
        var cap = DateTimeOffset.UtcNow.AddDays(push.SubscriptionTtlDays);
        var requested = body.Element(DavNames.Push + "expires")?.Value;
        var expiresAt = DateTimeOffset.TryParse(requested, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var wanted)
            && wanted < cap ? wanted : cap;

        // Re-registration UPDATES rather than duplicating (the unique index says so) — clients re-register
        // routinely, and a duplicate would send the same notification twice.
        var existing = await context.Db.DavPushSubscriptions
            .FirstOrDefaultAsync(s => s.FolderId == folderId && s.Endpoint == endpoint, context.Cancellation);
        if (existing is null)
        {
            existing = new DavPushSubscription
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId,
                FolderId = folderId,
                UserId = context.UserId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            context.Db.DavPushSubscriptions.Add(existing);
        }
        else
        {
            (existing.P256dh, existing.Auth, existing.ExpiresAt, existing.UserId) = (p256dh, auth, expiresAt, context.UserId);
        }

        await context.Db.SaveChangesAsync(context.Cancellation);

        context.Response.Headers.Location = $"{context.Request.Scheme}://{context.Request.Host}/dav/push-subscriptions/{existing.Id}";
        context.Response.Headers.Expires = expiresAt.ToString("R", CultureInfo.InvariantCulture);
        return new NoContentResult();
    }

    /// <summary>Unsubscribe. Idempotent — a client retrying a delete must not get an error.</summary>
    internal static async Task<IActionResult> UnregisterAsync(DavControllerContext context, Guid id)
    {
        var existing = await context.Db.DavPushSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == context.UserId, context.Cancellation);
        if (existing is not null)
        {
            context.Db.DavPushSubscriptions.Remove(existing);
            await context.Db.SaveChangesAsync(context.Cancellation);
        }

        return new NoContentResult();
    }

    private static IActionResult PushNotAvailable() => new ContentResult
    {
        StatusCode = StatusCodes.Status403Forbidden,
        ContentType = "application/xml; charset=utf-8",
        Content = new XDocument(
            new XElement(
                DavNames.Dav + "error",
                new XAttribute(XNamespace.Xmlns + "P", DavNames.Push.NamespaceName),
                new XElement(DavNames.Push + "push-not-available"))).ToString(),
    };
}
