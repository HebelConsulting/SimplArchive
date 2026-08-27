using System.Text.RegularExpressions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;

namespace SimplArchive.Api.WebDav;

// The LOCK/UNLOCK verb family (ADR "WebDAV hardening") beside the WebDavLockStore it drives (issue #466 moved
// it out of the middleware): acquire/refresh, release, the 423 gate for mutations, and the token/timeout
// header parsing. Static partial for the GeneratedRegex sources.
internal static partial class WebDavLockHandling
{
    // Real exclusive write locks (ADR "WebDAV hardening"): LOCK acquires (or refreshes the caller's own) lock on
    // the path; a lock held by a *different* owner returns 423 Locked. The token is returned for the client to
    // present (via If / Lock-Token) on subsequent mutations.
    internal const int DefaultLockSeconds = 3600;

    /// <summary>
    /// LOCK, including the <b>lock-null</b> case RFC 4918 §9.10.4 requires: a lock on an unmapped URL creates a
    /// locked, empty resource and answers <b>201 Created</b>.
    /// </summary>
    /// <remarks>
    /// This is how a word processor RESERVES a name before writing it, and answering 200 instead is what made
    /// atomic saving impossible (#762). Measured: <c>LOCK …/.~WRD1464</c> returned 200 — which says the resource
    /// already exists — while <c>PROPFIND</c> on the same path returned 404. Given two contradictory answers the
    /// editor abandoned the attempt and started again with a fresh collection, six times over, and then reported
    /// a network or permission error. Nothing failed; we simply told it two incompatible things.
    ///
    /// The lock-null body is an empty object in the same per-user area the path would be staged in, so it is
    /// visible to PROPFIND immediately and a later PUT just replaces it. Confined to paths that are ALREADY
    /// swallowed — inside a safe-save collection, or a name the clutter filter keeps out of the archive —
    /// because creating one anywhere else would mean inventing a Document nobody asked for.
    /// </remarks>
    internal static async Task HandleLockAsync(
        WebDavLockStore lockStore, IServiceProvider services, HttpContext context, User user, List<string> segments)
    {
        var created = false;
        if (segments.Count > 0 && ShadowableLeaf(segments))
        {
            var storage = services.GetRequiredService<IObjectStorageClient>();
            var key = WebDavClutter.IsUnderSafeSaveTemp(segments)
                ? WebDavSafeSave.FileKey(user, segments)
                : WebDavSafeSave.ShadowKey(user, segments);

            if (!await storage.ExistsAsync(key, context.RequestAborted))
            {
                await storage.PutObjectAsync(key, new MemoryStream([]), "application/octet-stream", context.RequestAborted);
                created = true;
            }
        }

        HandleLock(lockStore, context, user, segments, created);
    }

    /// <summary>Paths we already keep out of the archive, and can therefore hold a lock-null for.</summary>
    private static bool ShadowableLeaf(IReadOnlyList<string> segments) =>
        WebDavClutter.IsUnderSafeSaveTemp(segments)
        || WebDavClutter.IsOsClutter(segments[^1])
        || WebDavClutter.IsTransientClutter(segments[^1]);

    internal static void HandleLock(WebDavLockStore lockStore, HttpContext context, User user, List<string> segments, bool created = false)
    {
        var pathKey = PathKey(segments);
        var timeout = TimeSpan.FromSeconds(ParseTimeoutSeconds(context));
        var lockInfo = lockStore.TryLock(user.TenantId, pathKey, user.Id, timeout, DateTimeOffset.UtcNow);
        if (lockInfo is null)
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var seconds = (int)Math.Max(1, (lockInfo.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        var owner = WebDavXml.Xml(user.Email);
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><D:prop xmlns:D="DAV:"><D:lockdiscovery><D:activelock><D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope><D:depth>0</D:depth><D:owner>{owner}</D:owner><D:timeout>Second-{seconds}</D:timeout><D:locktoken><D:href>{lockInfo.Token}</D:href></D:locktoken></D:activelock></D:lockdiscovery></D:prop>""";
        context.Response.Headers["Lock-Token"] = $"<{lockInfo.Token}>";
        // 201 when the lock CREATED the resource (RFC 4918 §9.10.4), 200 when it locked one that was already
        // there. The difference is not cosmetic: 200 on an unmapped URL claims the resource exists, which the
        // next PROPFIND then contradicts.
        context.Response.StatusCode = created ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";
        context.Response.WriteAsync(xml).GetAwaiter().GetResult();
    }

    internal static void HandleUnlock(WebDavLockStore lockStore, HttpContext context, User user, List<string> segments)
    {
        var token = context.Request.Headers["Lock-Token"].ToString().Trim().Trim('<', '>');
        context.Response.StatusCode = lockStore.Unlock(user.TenantId, PathKey(segments), token)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status409Conflict;
    }

    // A mutating op is refused 423 Locked when a *different* owner holds an unexpired lock and the request didn't
    // present its token (in the If or Lock-Token header).
    internal static bool IsLocked(WebDavLockStore lockStore, HttpContext context, User user, List<string> segments)
    {
        return lockStore.IsBlocked(user.TenantId, PathKey(segments), user.Id, PresentedLockTokens(context), DateTimeOffset.UtcNow);
    }

    internal static string PathKey(List<string> segments) => string.Join("/", segments);

    internal static int ParseTimeoutSeconds(HttpContext context)
    {
        // Timeout: "Second-600" / "Infinite" (capped). Default 1 hour.
        var header = context.Request.Headers["Timeout"].ToString();
        var match = LockTimeoutRegex().Match(header);
        return match.Success && int.TryParse(match.Groups[1].Value, out var s) ? Math.Clamp(s, 1, 86400) : DefaultLockSeconds;
    }

    // Extracts the opaquelocktoken URIs a request presents in its If / Lock-Token headers (a pragmatic subset of
    // the full RFC 4918 If grammar — enough for common clients that echo the token they were issued).
    internal static IReadOnlyCollection<string> PresentedLockTokens(HttpContext context)
    {
        var raw = $"{context.Request.Headers["If"]} {context.Request.Headers["Lock-Token"]}";
        return LockTokenRegex().Matches(raw).Select(m => m.Value).ToHashSet();
    }

    [GeneratedRegex(@"opaquelocktoken:[0-9a-fA-F-]+")]
    internal static partial Regex LockTokenRegex();

    [GeneratedRegex(@"Second-(\d+)")]
    internal static partial Regex LockTimeoutRegex();
}
