// The DAV request handling, written ONCE against a DavProtocol (see DavProtocol) and routed by the two thin
// controllers beside this file. Ported in shape from SimplCalCon's Cal/CardDav*Controller pairs (Apache-2.0,
// ADR 0621), which are separate per protocol there because their storage is; here they differ only in
// constants, so the standing rule applies — one generic implementation, and the type-specific surface is one
// forwarding line per route.
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.CalDav.Http;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

internal static class DavEndpoints
{
    /// <summary>PROPFIND on the service root: name the principal, nothing more.</summary>
    internal static async Task<IActionResult> RootAsync(DavControllerContext context)
    {
        var request = PropRequest.Parse(await context.ReadBodyAsync());
        return context.MultiStatus(request, [DavResources.Root(context.Protocol, context.UserId)]);
    }

    /// <summary>PROPFIND on the principal: name the home set.</summary>
    internal static async Task<IActionResult> PrincipalAsync(DavControllerContext context)
    {
        var request = PropRequest.Parse(await context.ReadBodyAsync());
        return context.MultiStatus(request, [DavResources.Principal(context.Protocol, context.UserId, context.DisplayName)]);
    }

    /// <summary>PROPFIND on the home set: the collections the caller may see (Depth ≥ 1).</summary>
    internal static async Task<IActionResult> HomeAsync(DavControllerContext context)
    {
        var request = PropRequest.Parse(await context.ReadBodyAsync());
        var resources = new List<DavResource> { DavResources.Home(context.Protocol, context.UserId) };

        if (context.Depth >= 1)
        {
            foreach (var collection in await DavTree.CollectionsAsync(context.Db, context.Rights, context.UserId, context.Protocol, context.Cancellation))
            {
                var rights = await context.Rights.GetEffectiveRightsAsync(context.UserId, collection.FolderId);
                var sequence = await DavChangeLog.CurrentAsync(context.Db, collection.FolderId, context.Cancellation);
                resources.Add(DavResources.Collection(context.Protocol, context.UserId, collection, rights, sequence, context.VapidPublicKey));
            }
        }

        return context.MultiStatus(request, resources);
    }

    /// <summary>PROPFIND on one collection, and (Depth ≥ 1) its items.</summary>
    internal static async Task<IActionResult> CollectionAsync(DavControllerContext context, Guid folderId)
    {
        var collection = await DavTree.CollectionAsync(context.Db, context.Rights, context.UserId, context.Protocol, folderId, context.Cancellation);
        if (collection is null)
        {
            return new NotFoundResult();
        }

        var request = PropRequest.Parse(await context.ReadBodyAsync());
        var rights = await context.Rights.GetEffectiveRightsAsync(context.UserId, folderId);
        var sequence = await DavChangeLog.CurrentAsync(context.Db, folderId, context.Cancellation);
        var resources = new List<DavResource> { DavResources.Collection(context.Protocol, context.UserId, collection, rights, sequence, context.VapidPublicKey) };

        if (context.Depth >= 1)
        {
            foreach (var item in await DavTree.ItemsAsync(context.Db, context.Protocol, folderId, context.Cancellation))
            {
                resources.Add(DavResources.Item(context.Protocol, item, data: null));
            }
        }

        return context.MultiStatus(request, resources);
    }

    /// <summary>PROPFIND on one item.</summary>
    internal static async Task<IActionResult> ItemAsync(DavControllerContext context, Guid folderId, string resourceName)
    {
        if (await DavTree.CollectionAsync(context.Db, context.Rights, context.UserId, context.Protocol, folderId, context.Cancellation) is null)
        {
            return new NotFoundResult();
        }

        var item = await DavTree.ItemAsync(context.Db, context.Protocol, folderId, resourceName, context.Cancellation);
        if (item is null)
        {
            return new NotFoundResult();
        }

        var request = PropRequest.Parse(await context.ReadBodyAsync());
        return context.MultiStatus(request, [DavResources.Item(context.Protocol, item, data: null)]);
    }

    /// <summary>
    /// PROPPATCH: acknowledged and ignored (RFC 4918 §9.2). We persist no dead properties — but Apple's
    /// dataaccessd sets collection metadata during account setup and ABORTS when this 405s, which is exactly
    /// what the pre-port middleware did. This is the single most consequential thing the port fixes.
    /// </summary>
    internal static async Task<IActionResult> PropPatchAsync(DavControllerContext context, string href)
    {
        var body = await context.ReadBodyAsync();
        return DavXml.MultiStatus(MultiStatus.PropPatchAccepted(href, body));
    }

    /// <summary>
    /// RFC 6578 <c>sync-collection</c>: what changed since the client's token, and the new token to resume
    /// from. A REMOVED item is reported as its href with 404 — that is the whole point of the report, and the
    /// one thing a client cannot work out by re-listing, because the item is simply absent.
    /// </summary>
    internal static async Task<IActionResult> SyncAsync(DavControllerContext context, Guid folderId, System.Xml.Linq.XElement body)
    {
        var since = DavTokens.TryParse(body.Element(DavNames.SyncToken)?.Value);

        // An unparseable/foreign token is NOT an error to swallow: answering it as "everything" would silently
        // hand the client a full resync it did not ask for. RFC 6578 says 403 with valid-sync-token.
        if (body.Element(DavNames.SyncToken) is { } token && !string.IsNullOrWhiteSpace(token.Value) && since is null)
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status403Forbidden,
                ContentType = "application/xml; charset=utf-8",
                Content = """<?xml version="1.0" encoding="utf-8"?><D:error xmlns:D="DAV:"><D:valid-sync-token/></D:error>""",
            };
        }

        var request = PropRequest.FromProp(body.Element(DavNames.Prop));
        var changes = await DavChangeLog.SinceAsync(context.Db, folderId, since ?? 0, context.Cancellation);
        var current = await DavChangeLog.CurrentAsync(context.Db, folderId, context.Cancellation);

        var resources = new List<DavResource>();
        var removed = new List<string>();
        foreach (var change in changes)
        {
            if (change.ChangeType == Domain.CalDav.DavChangeType.Removed)
            {
                removed.Add(context.Protocol.ItemHref(folderId, change.ResourceName));
                continue;
            }

            var item = await DavTree.ItemAsync(context.Db, context.Protocol, folderId, change.ResourceName, context.Cancellation);
            if (item is null)
            {
                // Logged as changed but no longer there — tell the client it is gone rather than omitting it,
                // which would leave a stale copy on the device forever.
                removed.Add(context.Protocol.ItemHref(folderId, change.ResourceName));
                continue;
            }

            resources.Add(DavResources.Item(context.Protocol, item, data: null));
        }

        var document = MultiStatus.Build(request, resources);
        MultiStatus.AddNotFound(document, removed);
        MultiStatus.WithSyncToken(document, DavTokens.Format(current));
        return DavXml.MultiStatus(document);
    }

    /// <summary>
    /// REPORT: the multiget forms answer exactly the hrefs asked for, the query forms answer the whole
    /// collection (no server-side filtering yet — poll-based clients re-filter locally anyway). Both carry the
    /// item data inline, which is what saves the per-item GET round trips.
    /// </summary>
    /// <summary>
    /// The REPORT bodies this server actually answers. <c>sync-collection</c> is handled before this set is
    /// consulted, so it is not listed here.
    /// </summary>
    /// <remarks>
    /// Both protocols' names are in one set on purpose: sending a calendar-query to an addressbook is a client
    /// bug we would rather answer generously than refuse, and the collection it reads is the right one either
    /// way. What must NOT be generous is a report we do not implement at all.
    /// </remarks>
    private static readonly HashSet<System.Xml.Linq.XName> ServedReports =
    [
        DavNames.CalendarMultiget,
        DavNames.CalendarQuery,
        DavNames.AddressBookMultiget,
        DavNames.AddressBookQuery,
    ];

    /// <summary>
    /// The same log category the wire-trace middleware uses, so the warning and the switch it names are one
    /// knob rather than two an operator has to discover separately (ADR 0626).
    /// </summary>
    private const string WireCategory = "SimplArchive.Dav.Wire";

    private static ILogger Wire(DavControllerContext context) =>
        context.Request.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(WireCategory);

    private static string UserAgent(DavControllerContext context) =>
        context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : "(unknown)";

    internal static async Task<IActionResult> ReportAsync(DavControllerContext context, Guid folderId)
    {
        if (await DavTree.CollectionAsync(context.Db, context.Rights, context.UserId, context.Protocol, folderId, context.Cancellation) is null)
        {
            return new NotFoundResult();
        }

        var body = await context.ReadBodyAsync();
        if (body?.Name == DavNames.SyncCollection)
        {
            return await SyncAsync(context, folderId, body);
        }

        // Everything past here answers a multiget/query with the collection's items — so the REPORT type has to
        // be one we actually serve. Without this check an UNRECOGNISED report fell through to the same code and
        // was answered with a 207 full of collection data: we replied "here is your data" to a question we had
        // not understood, and the client believed it had succeeded (ADR 0626). A free-busy-query expects an
        // iCalendar body, not a multistatus; a principal search expects principals.
        //
        // RFC 3253 §3.6 says what to do instead: 403 with the DAV:supported-report precondition, which tells the
        // client WHICH rule it broke and lets it fall back.
        if (body is not null && !ServedReports.Contains(body.Name))
        {
            Wire(context).LogWarning(
                "Unsupported DAV REPORT {Report} from client {UserAgent} on {Protocol} — refused with 403 "
                + "supported-report. The client will not get what it asked for; set {Category}=Verbose to log "
                + "the full request/response.",
                body.Name.ToString(), UserAgent(context), context.Protocol.BasePath, WireCategory);

            return DavXml.PreconditionFailure(DavNames.SupportedReport);
        }

        var request = PropRequest.FromProp(body?.Element(DavNames.Prop));
        var wanted = body?.Elements(DavNames.Href).Select(h => h.Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var items = await DavTree.ItemsAsync(context.Db, context.Protocol, folderId, context.Cancellation);
        if (wanted.Count > 0)
        {
            items = items.Where(i =>
                wanted.Contains(context.Protocol.ItemHref(folderId, i.ResourceName))
                || wanted.Contains(Uri.UnescapeDataString(context.Protocol.ItemHref(folderId, i.ResourceName)))).ToList();
        }

        var resources = new List<DavResource>();
        foreach (var item in items)
        {
            var data = await context.ReadItemAsync(item);
            if (data is not null)
            {
                resources.Add(DavResources.Item(context.Protocol, item, data));
            }
        }

        return context.MultiStatus(request, resources);
    }

    /// <summary>GET/HEAD one item: its stored bytes, with the ETag a client conditions on.</summary>
    internal static async Task<IActionResult> GetAsync(DavControllerContext context, Guid folderId, string resourceName, bool body)
    {
        if (await DavTree.CollectionAsync(context.Db, context.Rights, context.UserId, context.Protocol, folderId, context.Cancellation) is null)
        {
            return new NotFoundResult();
        }

        var item = await DavTree.ItemAsync(context.Db, context.Protocol, folderId, resourceName, context.Cancellation);
        if (item is null)
        {
            return new NotFoundResult();
        }

        // Seeing a collection is not reading its items — the same split the workbench and WebDAV apply.
        if (!(await context.Rights.GetEffectiveRightsAsync(context.UserId, item.DocumentId)).CanReadContent)
        {
            return new ForbidResult(Authentication.DavAuthenticationDefaults.Scheme);
        }

        var data = await context.ReadItemAsync(item);
        if (data is null)
        {
            return new NotFoundResult();
        }

        context.SetItemHeaders(item);
        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = context.Protocol.ContentType,
            Content = body ? data : string.Empty,
        };
    }

    /// <summary>PUT: a new VERSION of the UID-matched item, or a new one. Semantics per ADR 0620.</summary>
    internal static Task<IActionResult> PutAsync(DavControllerContext context, IServiceProvider services, Guid folderId, string resourceName) =>
        DavWrites.PutAsync(context, services, folderId, resourceName);

    /// <summary>DELETE: a soft delete into the recycle bin, gated on CanDelete and legal holds.</summary>
    internal static Task<IActionResult> DeleteAsync(DavControllerContext context, IServiceProvider services, Guid folderId, string resourceName) =>
        DavWrites.DeleteAsync(context, services, folderId, resourceName);
}
