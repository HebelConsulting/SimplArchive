using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.WebDav;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

/// <summary>
/// The CalDAV + CardDAV gateway (#564 slice 1, ADR 0619) — read-only in this slice. One implementation over
/// <see cref="DavProtocol"/>, so the two protocols cannot drift: /caldav serves Calendars as calendar
/// collections, /carddav serves Addressbooks as address books, each listing every typed folder the caller
/// holds CanSee on, wherever it sits in the archive tree.
/// </summary>
/// <remarks>
/// Auth is the SHARED DAV password (the epic's decision): the same generated WebDAV credential covers WebDAV,
/// CalDAV and CardDAV — one secret to keep, one revocation. Discovery follows the two well-known URIs, which
/// the middleware answers before authentication (a client probes them unauthenticated first).
/// </remarks>
public sealed class CalDavMiddleware
{
    private const string DavNamespaceDeclarations =
        "xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CARD=\"urn:ietf:params:xml:ns:carddav\" xmlns:CS=\"http://calendarserver.org/ns/\" xmlns:IC=\"http://apple.com/ns/ical/\"";

    private readonly RequestDelegate _next;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public CalDavMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // The two well-known discovery URIs (RFC 6764) — answered before auth, since a client probes them
        // with no credentials and expects to be pointed at the protocol root.
        foreach (var wellKnown in DavProtocol.All)
        {
            if (path.Equals($"/.well-known/{wellKnown.BasePath.TrimStart('/')}", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect(wellKnown.BasePath + "/", permanent: true);
                return;
            }
        }

        var protocol = DavProtocol.ForPath(path);
        if (protocol is null)
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method.ToUpperInvariant();
        var services = context.RequestServices;
        var db = services.GetRequiredService<SimplArchiveDbContext>();

        // OPTIONS advertises compliance before auth — some clients probe it first to decide whether to bother.
        if (method == "OPTIONS")
        {
            WriteAllowHeaders(context, protocol);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var user = await AuthenticateAsync(context, db);
        if (user is null)
        {
            context.Response.Headers["WWW-Authenticate"] = $"Basic realm=\"SimplArchive {protocol.NamespacePrefix}DAV\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Everything past auth runs as that user, so the tenant query filter scopes reads exactly as it would
        // for the workbench — the same posture the WebDAV gateway and the IMAP session take.
        ((CurrentTenantAccessor)services.GetRequiredService<ICurrentTenantAccessor>()).TenantId = user.TenantId;
        ((CurrentUserAccessor)services.GetRequiredService<ICurrentUserAccessor>()).UserId = user.Id;

        var segments = path[protocol.BasePath.Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var rights = services.GetRequiredService<IEffectiveRightsCalculator>();

        switch (method)
        {
            case "PROPFIND":
                await HandlePropFindAsync(context, db, rights, user, protocol, segments);
                break;
            case "REPORT":
                await HandleReportAsync(context, db, rights, user, protocol, segments);
                break;
            case "GET":
            case "HEAD":
                await HandleGetAsync(context, db, rights, services, user, protocol, segments, body: method == "GET");
                break;
            case "PUT":
            case "DELETE":
                await HandleWriteAsync(context, db, rights, services, user, protocol, segments, method);
                break;
            default:
                WriteAllowHeaders(context, protocol);
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                break;
        }
    }

    private static void WriteAllowHeaders(HttpContext context, DavProtocol protocol)
    {
        context.Response.Headers["DAV"] = $"1, 3, {protocol.DavCompliance}";
        context.Response.Headers["Allow"] = "OPTIONS, PROPFIND, REPORT, GET, HEAD, PUT, DELETE";
    }

    // The SHARED DAV password (epic decision): the WebDAV credential authenticates all three DAV surfaces.
    private async Task<User?> AuthenticateAsync(HttpContext context, SimplArchiveDbContext db)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return null;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return null;
        }

        var normalized = decoded[..separator].ToUpperInvariant();
        var password = decoded[(separator + 1)..];

        // No tenant is known yet — the standing pre-tenant-lookup rule applies (ADR 0150 / TokenController).
        var user = await db.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive);
        return user?.WebDavPasswordHash is not null
            && _passwordHasher.VerifyHashedPassword(user, user.WebDavPasswordHash, password) != PasswordVerificationResult.Failed
                ? user
                : null;
    }

    // ---- PROPFIND ------------------------------------------------------------------------------------

    private static async Task HandlePropFindAsync(
        HttpContext context, SimplArchiveDbContext db, IEffectiveRightsCalculator rights, User user, DavProtocol protocol, List<string> segments)
    {
        var depth = context.Request.Headers["Depth"].ToString();
        var responses = new List<PropStatXml>();

        // /{protocol}                       → the service root, whose only job is to name the principal
        // /{protocol}/principals/{userId}/  → the principal, whose job is to name the home set
        // /{protocol}/{collections}/        → the home set: every visible typed folder (Depth 1)
        // /{protocol}/{collections}/{id}/   → one collection and (Depth 1) its items
        switch (segments)
        {
            case []:
                responses.Add(new PropStatXml(protocol.BasePath + "/", "HTTP/1.1 200 OK", RootProps(protocol, user)));
                break;

            case ["principals", _, ..]:
                responses.Add(new PropStatXml(protocol.PrincipalHref(user.Id), "HTTP/1.1 200 OK", PrincipalProps(protocol, user)));
                break;

            case [var collections] when collections.Equals(protocol.CollectionsSegment, StringComparison.OrdinalIgnoreCase):
                responses.Add(new PropStatXml(protocol.HomeSetHref(), "HTTP/1.1 200 OK", HomeSetProps()));
                if (depth != "0")
                {
                    foreach (var collection in await DavTree.CollectionsAsync(db, rights, user.Id, protocol, context.RequestAborted))
                    {
                        responses.Add(new PropStatXml(
                            protocol.CollectionHref(collection.FolderId), "HTTP/1.1 200 OK", CollectionProps(protocol, collection)));
                    }
                }

                break;

            case [var collections, var folderSegment, ..] when collections.Equals(protocol.CollectionsSegment, StringComparison.OrdinalIgnoreCase):
                if (!Guid.TryParse(folderSegment, out var folderId))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var target = await DavTree.CollectionAsync(db, rights, user.Id, protocol, folderId, context.RequestAborted);
                if (target is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // A PROPFIND on the item itself (segments = [collections, folderId, resourceName]) resolves
                // that one item rather than listing the collection to find it.
                if (segments.Count > 2)
                {
                    var resourceName = Uri.UnescapeDataString(segments[2]);
                    var item = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.RequestAborted);
                    if (item is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    responses.Add(new PropStatXml(
                        protocol.ItemHref(folderId, item.ResourceName), "HTTP/1.1 200 OK", ItemProps(protocol, item)));
                    break;
                }

                responses.Add(new PropStatXml(protocol.CollectionHref(folderId), "HTTP/1.1 200 OK", CollectionProps(protocol, target)));
                if (depth != "0")
                {
                    foreach (var item in await DavTree.ItemsAsync(db, protocol, folderId, context.RequestAborted))
                    {
                        responses.Add(new PropStatXml(
                            protocol.ItemHref(folderId, item.ResourceName), "HTTP/1.1 200 OK", ItemProps(protocol, item)));
                    }
                }

                break;

            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }

        await WriteMultiStatusAsync(context, responses);
    }

    // ---- REPORT --------------------------------------------------------------------------------------

    // calendar-query / addressbook-query answer with the whole collection (no server-side filtering in this
    // slice — a client re-filters locally, which is what the poll-based ones do anyway); the multiget forms
    // answer exactly the hrefs asked for. Both carry the item's data inline, which is what saves the round
    // trips a per-item GET would cost.
    private static async Task HandleReportAsync(
        HttpContext context, SimplArchiveDbContext db, IEffectiveRightsCalculator rights, User user, DavProtocol protocol, List<string> segments)
    {
        if (segments is not [var collections, var folderSegment, ..]
            || !collections.Equals(protocol.CollectionsSegment, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(folderSegment, out var folderId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (await DavTree.CollectionAsync(db, rights, user.Id, protocol, folderId, context.RequestAborted) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        var requestedHrefs = ExtractHrefs(body);

        var items = await DavTree.ItemsAsync(db, protocol, folderId, context.RequestAborted);
        if (requestedHrefs.Count > 0)
        {
            items = items
                .Where(i => requestedHrefs.Contains(protocol.ItemHref(folderId, i.ResourceName), StringComparer.OrdinalIgnoreCase)
                    || requestedHrefs.Contains(Uri.UnescapeDataString(protocol.ItemHref(folderId, i.ResourceName)), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var storage = context.RequestServices.GetRequiredService<IObjectStorageClient>();
        var responses = new List<PropStatXml>();
        foreach (var item in items)
        {
            var data = await ReadItemAsync(storage, item, context.RequestAborted);
            if (data is null)
            {
                continue;
            }

            var dataElement = $"<{protocol.NamespacePrefix}:{protocol.CollectionResourceType}-data>{WebDavXml.Xml(data)}</{protocol.NamespacePrefix}:{protocol.CollectionResourceType}-data>";
            responses.Add(new PropStatXml(
                protocol.ItemHref(folderId, item.ResourceName),
                "HTTP/1.1 200 OK",
                $"<D:getetag>\"{WebDavXml.Xml(item.ETag)}\"</D:getetag>{dataElement}"));
        }

        await WriteMultiStatusAsync(context, responses);
    }

    // The hrefs a multiget asks for. Parsed with XmlReader rather than a regex: the element is namespace-
    // qualified (DAV:href) and clients differ in prefix.
    private static HashSet<string> ExtractHrefs(string body)
    {
        var hrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
        {
            return hrefs;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "href", NamespaceURI: "DAV:" })
                {
                    hrefs.Add(reader.ReadElementContentAsString().Trim());
                }
            }
        }
        catch (XmlException)
        {
            // A malformed REPORT body degrades to "the whole collection" rather than failing the sync.
        }

        return hrefs;
    }

    // ---- PUT / DELETE --------------------------------------------------------------------------------

    private static async Task HandleWriteAsync(
        HttpContext context, SimplArchiveDbContext db, IEffectiveRightsCalculator rights, IServiceProvider services,
        User user, DavProtocol protocol, List<string> segments, string method)
    {
        // Both verbs address ONE item: /{protocol}/{collections}/{folderId}/{resourceName}. A write to the
        // collection itself is not offered — the archive tree is shaped in the app, not by a sync client
        // (the same rule the IMAP endpoint applies to mailboxes).
        if (segments is not [var collections, var folderSegment, var resourceSegment]
            || !collections.Equals(protocol.CollectionsSegment, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(folderSegment, out var folderId))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // The collection must be one the caller can see, and really wear the protocol's folder mask.
        if (await DavTree.CollectionAsync(db, rights, user.Id, protocol, folderId, context.RequestAborted) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var resourceName = Uri.UnescapeDataString(resourceSegment);
        if (method == "PUT")
        {
            await DavWrites.PutAsync(context, db, rights, services, user, protocol, folderId, resourceName);
        }
        else
        {
            await DavWrites.DeleteAsync(context, db, rights, services, user, protocol, folderId, resourceName);
        }
    }

    // ---- GET -----------------------------------------------------------------------------------------

    private static async Task HandleGetAsync(
        HttpContext context, SimplArchiveDbContext db, IEffectiveRightsCalculator rights, IServiceProvider services,
        User user, DavProtocol protocol, List<string> segments, bool body)
    {
        if (segments is not [var collections, var folderSegment, var resourceSegment]
            || !collections.Equals(protocol.CollectionsSegment, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(folderSegment, out var folderId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var collection = await DavTree.CollectionAsync(db, rights, user.Id, protocol, folderId, context.RequestAborted);
        if (collection is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Resolved directly, never by listing the collection — a syncing client comes here once per item.
        var resourceName = Uri.UnescapeDataString(resourceSegment);
        var item = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.RequestAborted);
        if (item is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Reading the bytes needs CanReadContent — CanSee is enough to know a collection exists, not to read
        // its items (the same split the workbench and WebDAV apply).
        if (!(await rights.GetEffectiveRightsAsync(user.Id, item.DocumentId)).CanReadContent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var data = await ReadItemAsync(services.GetRequiredService<IObjectStorageClient>(), item, context.RequestAborted);
        if (data is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = protocol.ContentType;
        context.Response.Headers.ETag = $"\"{item.ETag}\"";
        context.Response.Headers.LastModified = item.LastModified.UtcDateTime.ToString("R");
        var bytes = Encoding.UTF8.GetBytes(data);
        context.Response.ContentLength = bytes.Length;
        if (body)
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
    }

    private static async Task<string?> ReadItemAsync(IObjectStorageClient storage, DavItem item, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await storage.GetObjectAsync(item.ObjectKey, cancellationToken);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception)
        {
            // A missing blob is a gap in one item, not a failed sync — it is dropped from the response.
            return null;
        }
    }

    // ---- Property bodies -----------------------------------------------------------------------------

    private static string RootProps(DavProtocol protocol, User user) =>
        $"<D:resourcetype><D:collection/></D:resourcetype>"
        + $"<D:current-user-principal><D:href>{WebDavXml.Xml(protocol.PrincipalHref(user.Id))}</D:href></D:current-user-principal>"
        + $"<D:principal-URL><D:href>{WebDavXml.Xml(protocol.PrincipalHref(user.Id))}</D:href></D:principal-URL>";

    private static string PrincipalProps(DavProtocol protocol, User user) =>
        "<D:resourcetype><D:collection/><D:principal/></D:resourcetype>"
        + $"<D:displayname>{WebDavXml.Xml(user.DisplayName)}</D:displayname>"
        + $"<D:current-user-principal><D:href>{WebDavXml.Xml(protocol.PrincipalHref(user.Id))}</D:href></D:current-user-principal>"
        + $"<D:principal-URL><D:href>{WebDavXml.Xml(protocol.PrincipalHref(user.Id))}</D:href></D:principal-URL>"
        + $"<{protocol.NamespacePrefix}:{protocol.HomeSetProperty}><D:href>{WebDavXml.Xml(protocol.HomeSetHref())}</D:href></{protocol.NamespacePrefix}:{protocol.HomeSetProperty}>"
        + $"<D:principal-address><D:href>{WebDavXml.Xml(protocol.PrincipalHref(user.Id))}</D:href></D:principal-address>";

    private static string HomeSetProps() =>
        "<D:resourcetype><D:collection/></D:resourcetype><D:displayname>SimplArchive</D:displayname>";

    private static string CollectionProps(DavProtocol protocol, DavCollection collection) =>
        $"<D:resourcetype><D:collection/><{protocol.NamespacePrefix}:{protocol.CollectionResourceType}/></D:resourcetype>"
        + $"<D:displayname>{WebDavXml.Xml(collection.DisplayName)}</D:displayname>"
        + (protocol == DavProtocol.CalDav
            ? "<C:supported-calendar-component-set><C:comp name=\"VEVENT\"/></C:supported-calendar-component-set>"
            : string.Empty)
        // What REPORTs this collection answers. A client (DAVx⁵ among them) probes this before deciding
        // whether it may use multiget at all — omitting it makes a capable server look incapable, so it is
        // advertised even though slice 2 has no sync-collection yet (that arrives with slice 3).
        + $"<D:supported-report-set><D:supported-report><D:report><{protocol.NamespacePrefix}:{protocol.MultigetReport}/></D:report></D:supported-report>"
        + $"<D:supported-report><D:report><{protocol.NamespacePrefix}:{protocol.QueryReport}/></D:report></D:supported-report></D:supported-report-set>"
        // The colour, in the namespace every calendar/contacts client actually reads it from (ADR 0620).
        + (collection.Color is { Length: > 0 } color
            ? $"<IC:calendar-color>{WebDavXml.Xml(color)}</IC:calendar-color>"
            : string.Empty)
        // What the caller actually holds on this collection (#564 slice 2) — a client that sees only <read/>
        // disables its own new/edit affordances, which is exactly right for a read-only collection.
        // The privileges the caller actually holds. A client checks for BIND before offering "new item" and
        // UNBIND before offering delete — reporting only <write/> (as a first cut did) leaves some clients
        // read-only despite the rights; the sister project's DavPrivileges maps the same way.
        + "<D:current-user-privilege-set><D:privilege><D:read/></D:privilege>"
        + (collection.Writable
            ? "<D:privilege><D:write/></D:privilege><D:privilege><D:write-content/></D:privilege>"
              + "<D:privilege><D:bind/></D:privilege><D:privilege><D:unbind/></D:privilege>"
            : string.Empty)
        + "</D:current-user-privilege-set>";

    private static string ItemProps(DavProtocol protocol, DavItem item) =>
        $"<D:resourcetype/><D:getetag>\"{WebDavXml.Xml(item.ETag)}\"</D:getetag>"
        + $"<D:getcontenttype>{WebDavXml.Xml(protocol.ContentType)}</D:getcontenttype>"
        + (item.SizeBytes is { } size ? $"<D:getcontentlength>{size}</D:getcontentlength>" : string.Empty)
        + $"<D:getlastmodified>{item.LastModified.UtcDateTime:R}</D:getlastmodified>";

    // The DAV: multistatus, with the CalDAV/CardDAV namespaces declared up front — WebDavXml's writer declares
    // only DAV:, and these responses carry elements from three namespaces.
    private static async Task WriteMultiStatusAsync(HttpContext context, List<PropStatXml> responses)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append($"<D:multistatus {DavNamespaceDeclarations}>");
        foreach (var response in responses)
        {
            sb.Append("<D:response>");
            sb.Append($"<D:href>{WebDavXml.Xml(response.Href)}</D:href>");
            sb.Append($"<D:propstat><D:prop>{response.Props}</D:prop><D:status>{response.Status}</D:status></D:propstat>");
            sb.Append("</D:response>");
        }

        sb.Append("</D:multistatus>");
        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(sb.ToString(), context.RequestAborted);
    }
}
