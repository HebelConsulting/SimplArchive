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
                resources.Add(DavResources.Collection(context.Protocol, context.UserId, collection, rights));
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
        var resources = new List<DavResource> { DavResources.Collection(context.Protocol, context.UserId, collection, rights) };

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
    /// REPORT: the multiget forms answer exactly the hrefs asked for, the query forms answer the whole
    /// collection (no server-side filtering yet — poll-based clients re-filter locally anyway). Both carry the
    /// item data inline, which is what saves the per-item GET round trips.
    /// </summary>
    internal static async Task<IActionResult> ReportAsync(DavControllerContext context, Guid folderId)
    {
        if (await DavTree.CollectionAsync(context.Db, context.Rights, context.UserId, context.Protocol, folderId, context.Cancellation) is null)
        {
            return new NotFoundResult();
        }

        var body = await context.ReadBodyAsync();
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
