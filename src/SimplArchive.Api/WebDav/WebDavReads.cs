using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;


namespace SimplArchive.Api.WebDav;

/// <summary>
/// The WebDAV read verbs: PROPFIND, GET and HEAD, and the property XML they answer with.
/// </summary>
/// <remarks>
/// Uses no middleware instance state at all — which is what let it move as a family, the same test ADR 0572
/// applied to <see cref="WebDavSpecialHandlers"/>. <see cref="WebDavPropFind"/> is a different thing and stays
/// separate: it PARSES the request and applies a prop set; this builds the answers.
/// </remarks>
internal static class WebDavReads
{
    // ---- PROPFIND ------------------------------------------------------------------------------------------
    internal static async Task HandlePropFindAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, string basePath)
    {
        var depth = context.Request.Headers["Depth"].ToString();
        depth = string.IsNullOrEmpty(depth) ? "1" : depth; // some clients omit Depth; default 1

        // The special Personal/Intray and Personal/Check-out folders are backed by object storage / the check-out
        // entity, not the Document tree (ADR "WebDAV Inbox + Check-out folders").
        if (WebDavMiddleware.IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialPropFindAsync(context, services, db, user, segments, depth, basePath);
            return;
        }

        // Staged aside by an atomic save (#794): the editor moved this document into its scratch collection and
        // we answered 201, so the honest answer here is that the path is gone. Saying otherwise is what made the
        // editor's identity follow the move while the name it needed never came free.
        if (await WebDavSafeSave.IsSetAsideAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The collection and its contents, once it has been created. Before that — while the editor is PROBING
        // candidate names for a free one — nothing is recorded, so this falls through to the ordinary 404 that
        // tells it the name is available. Both answers matter, and they are distinguished by whether MKCOL has
        // happened, not by the name's shape.
        if (WebDavClutter.IsSafeSaveScope(segments))
        {
            var storage = services.GetRequiredService<IObjectStorageClient>();
            if (await WebDavSafeSave.ExistsAsync(storage, user, segments, context.RequestAborted))
            {
                if (WebDavSafeSave.IsCollectionItself(segments))
                {
                    // Fetched before the depth check, not inside it: the scratch collection's own modified time is
                    // the newest of its members, so Depth 0 needs the listing too (#794).
                    var members = await WebDavSafeSave.FilesAsync(storage, user, segments);
                    var collectionProps = new List<PropStatXml>
                    {
                        CollectionProp(basePath, segments, segments[^1], NewestOf(members.Select(f => f.Modified))),
                    };
                    if (depth != "0")
                    {
                        collectionProps.AddRange(members.Select(f =>
                            FileProp(basePath, [.. segments, f.Name], f.Size, f.Modified, ContentTypes.ForExtension(Path.GetExtension(f.Name)))));
                    }

                    await WebDavXml.WriteMultiStatusAsync(context, collectionProps);
                    return;
                }

                var staged = (await WebDavSafeSave.FilesAsync(storage, user, segments)).FirstOrDefault(f => f.Name == segments[^1]);
                await WebDavXml.WriteMultiStatusAsync(context, [FileProp(basePath, segments, staged.Size, staged.Modified, ContentTypes.ForExtension(Path.GetExtension(segments[^1])))]);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // NOTE on the ANSWER ABOVE: it must depend on whether MKCOL happened. Answering 207
        // ("it exists") was added on the speculation that an editor verifies its scratch directory — and the
        // wire says the opposite. Its first move is to PROBE for a FREE name:
        //
        //     PROPFIND …/Test123.docx.sb-43a5b669-ZdBc5Y → 207
        //     PROPFIND …/Test123.docx.sb-43a5b669-adBc5Y → 207
        //     PROPFIND …/Test123.docx.sb-43a5b669-bdBc5Y → 207   …and on, and on
        //
        // Answering "exists" to every candidate means no candidate is ever free, so it never reaches MKCOL and
        // saving hangs. The truthful answer is the useful one: the collection does not exist, so 404, which is
        // what the ordinary resolution below already returns.
        var calc = services.GetRequiredService<IEffectiveRightsCalculator>();
        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (node is null)
        {
            // Anything we ACCEPTED but did not file — OS clutter, or the OS's zero-byte placeholder for a file
            // it is about to write. Serving it back is the whole point: a 404 for a write we answered 201 to is
            // what breaks an atomic save, because the editor finds no original to replace.
            var shadowStorage = services.GetRequiredService<IObjectStorageClient>();
            var shadowKey = segments.Count > 0 && WebDavClutter.IsTransientClutter(segments[^1])
                ? WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1]
                : WebDavSafeSave.ShadowKey(user, segments);
            if (segments.Count > 0 && await shadowStorage.ExistsAsync(shadowKey, context.RequestAborted))
            {
                var meta = (await shadowStorage.ListObjectsAsync(shadowKey)).FirstOrDefault();
                await WebDavXml.WriteMultiStatusAsync(context, [FileProp(
                    basePath, segments, meta?.Size ?? 0, meta?.LastModified ?? DateTimeOffset.UtcNow,
                    ContentTypes.ForExtension(Path.GetExtension(segments[^1])))]);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // ACL: hide a resource the caller can't see (ADR "WebDAV hardening") — 404 rather than leaking it.
        if (node.Document is { } targetDoc && !(await calc.GetEffectiveRightsAsync(user.Id, targetDoc.Id)).CanSee)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var propStorage = services.GetRequiredService<IObjectStorageClient>();
        var responses = new List<PropStatXml>
        {
            PropFor(node, segments, basePath,
                await WebDavSafeSave.WorkingCopyAsync(propStorage, user, node.Document?.Id, node.Document?.CheckedOutByUserId, context.RequestAborted)),
        };
        if (depth != "0" && node.IsCollection)
        {
            foreach (var child in await WebDavPathResolver.ChildrenAsync(db, user, node, calc, services.GetRequiredService<ILogger<WebDavMiddleware>>()))
            {
                List<string> childSegments = [.. segments, child.WebDavName];

                // …and it is gone from the LISTING too, not merely from a direct lookup. A client that finds the
                // path 404 and the parent still advertising it has been told both things at once, which is the
                // shape of defect this whole issue is made of.
                if (await WebDavSafeSave.IsSetAsideAsync(propStorage, user, childSegments, context.RequestAborted))
                {
                    continue;
                }

                responses.Add(PropFor(child, childSegments, basePath,
                    await WebDavSafeSave.WorkingCopyAsync(propStorage, user, child.Document?.Id, child.Document?.CheckedOutByUserId, context.RequestAborted)));
            }

            // The caller's own in-flight scratch entries — save collections and remembered sidecars. Every one
            // of these answers PROPFIND, GET and LOCK directly, and a listing that omits what a direct request
            // finds is the same defect this issue is made of, one level up: measured (#794), the OS re-listed
            // the folder mid-save, its cache dropped the collection the editor was standing in, and the editor
            // abandoned it — every cycle, forever. Per user, so nobody sees a colleague's save debris.
            foreach (var member in await WebDavSafeSave.ScratchMembersAsync(propStorage, user, segments))
            {
                List<string> memberSegments = [.. segments, member.Name];
                responses.Add(member.IsCollection
                    ? CollectionProp(basePath, memberSegments, member.Name, member.Modified)
                    : FileProp(basePath, memberSegments, member.Size, member.Modified,
                        ContentTypes.ForExtension(Path.GetExtension(member.Name))));
            }
        }

        await WebDavXml.WriteMultiStatusAsync(context, responses);
    }

    /// <param name="workingCopy">
    /// The caller's in-flight edit, when there is one. It supplies BOTH the length and the modification time —
    /// pairing its length with the DOCUMENT's timestamp is what made two same-length saves indistinguishable
    /// to an editor asking `getetag` whether its write landed (#794).
    /// </param>
    private static PropStatXml PropFor(WebDavNode node, List<string> segments, string basePath, (long Size, DateTimeOffset Modified, string? ETag)? workingCopy = null)
    {
        var href = WebDavPathResolver.HrefFor(basePath, segments) + (node.IsCollection ? "/" : "");
        var props = new StringBuilder();
        props.Append($"<D:displayname>{WebDavXml.Xml(node.WebDavName)}</D:displayname>");
        props.Append(node.IsCollection
            ? "<D:resourcetype><D:collection/></D:resourcetype>"
            : "<D:resourcetype/>");

        // What the caller would DOWNLOAD, which is the working copy when one exists — so the listing describes
        // the same bytes the GET serves, and the two cannot disagree about the resource's identity.
        var length = workingCopy?.Size ?? node.Length;
        var changed = workingCopy?.Modified ?? node.Modified;
        if (!node.IsCollection)
        {
            props.Append($"<D:getcontentlength>{length}</D:getcontentlength>");
            props.Append($"<D:getcontenttype>{WebDavXml.Xml(node.ContentType)}</D:getcontenttype>");
            props.Append($"<D:getetag>{WebDavXml.Xml(WebDavETag.For(length, changed, workingCopy?.ETag))}</D:getetag>");
        }

        var modified = changed.ToString("R", CultureInfo.InvariantCulture);
        props.Append($"<D:getlastmodified>{modified}</D:getlastmodified>");
        props.Append($"<D:creationdate>{node.Created.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</D:creationdate>");
        props.Append(SupportedLockXml); // advertise write-lock capability so editors open repository files read/write
        return new PropStatXml(href, "HTTP/1.1 200 OK", props.ToString());
    }

    // ---- GET / HEAD ----------------------------------------------------------------------------------------
    internal static async Task HandleGetAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, bool body)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();

        // Staged aside by an atomic save (#794) — the same answer PROPFIND gives, because a path cannot be
        // missing to one verb and present to another. The bytes are at the backup path the editor moved them to.
        if (!WebDavMiddleware.IsSpecialPath(context, segments)
            && await WebDavSafeSave.IsSetAsideAsync(storage, user, segments, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Anything ACCEPTED but not filed has to be READABLE, not merely listable. This was the last verb still
        // answering 404 to a write we had returned 201 for, and macOS reads back everything it writes: after the
        // set-aside was accepted, four consecutive `GET …/~WRL3914` returned 404, and Word showed "Saved" and
        // then reverted to unsaved. Same defect as every other one in #762 — telling a client two different
        // things about one path — and GET is where it stayed longest because listing was fixed first.
        if (segments.Count > 0 && !WebDavMiddleware.IsSpecialPath(context, segments))
        {
            var swallowed = WebDavClutter.IsUnderSafeSaveTemp(segments)
                ? WebDavSafeSave.FileKey(user, segments)
                : WebDavClutter.IsTransientClutter(segments[^1])
                    ? WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1]
                    : WebDavSafeSave.ShadowKey(user, segments);

            if (await storage.ExistsAsync(swallowed, context.RequestAborted)
                && await WebDavPathResolver.ResolveAsync(db, user, segments) is null)
            {
                var meta = (await storage.ListObjectsAsync(swallowed)).FirstOrDefault();
                await StreamAsync(context, storage, swallowed,
                    ContentTypes.ForExtension(Path.GetExtension(segments[^1])),
                    meta?.Size ?? 0, meta?.LastModified ?? DateTimeOffset.UtcNow, body);
                return;
            }
        }

        if (WebDavMiddleware.IsSpecialPath(context, segments))
        {
            if (segments.Count != 3 || await WebDavUserAreas.ResolveSpecialFileAsync(storage, db, user, segments[1], segments[2], segments) is not { } file)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await StreamAsync(context, storage, file.Key, ContentTypes.ForExtension(Path.GetExtension(file.Name)), file.Size, file.Modified, body);
            return;
        }

        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (node.IsCollection)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; // GET on a collection isn't supported
            return;
        }

        // A document the CALLER has checked out serves their WORKING COPY, not the last checked-in version
        // (#762). The mount then behaves like a filesystem: what you saved is what you read back. Without this,
        // every save over WebDAV — which is a working copy by design (ADR 0562) — would read back as the
        // previous content, and a user would rightly call that losing their work.
        //
        // Per user, deliberately: a colleague browsing the same folder sees the archived version, because a
        // working copy is one person's unfinished edit and not yet a fact about the document. Exactly the rule
        // the Check-out FOLDER already applies (WebDavUserAreas.CheckoutFilesAsync); this is it one level out,
        // where the document actually lives.
        if (node.Document is { CheckedOutByUserId: { } holder } && holder == user.Id)
        {
            var stashKey = CheckoutStashKey.Build(user.TenantId, user.Id, node.Document.Id);
            if (await WebDavSafeSave.WorkingCopyAsync(storage, user, node.Document.Id, node.Document.CheckedOutByUserId, context.RequestAborted) is { } working)
            {
                // The stash's OWN timestamp, so the ETag here matches the one PROPFIND reported (#794).
                await StreamAsync(context, storage, stashKey, node.ContentType, working.Size, working.Modified, body, working.ETag);
                return;
            }
        }

        await StreamAsync(context, storage, node.ObjectKey!, node.ContentType, node.Length, node.Modified, body);
    }

    // Streams an object as a 200 (full) or a 206 Partial Content response, honoring a single Range header
    // (ADR "WebDAV hardening"); an unsatisfiable range → 416. Advertises Accept-Ranges: bytes either way.
    private static async Task StreamAsync(HttpContext context, IObjectStorageClient storage, string key, string contentType, long size, DateTimeOffset modified, bool body, string? contentTag = null)
    {
        context.Response.ContentType = contentType;
        context.Response.Headers["Last-Modified"] = modified.ToString("R", CultureInfo.InvariantCulture);
        context.Response.Headers["ETag"] = WebDavETag.For(size, modified, contentTag);
        context.Response.Headers["Accept-Ranges"] = "bytes";

        var (present, ok, from, to) = ParseRange(context.Request.Headers["Range"].ToString(), size);
        if (present && !ok)
        {
            context.Response.Headers["Content-Range"] = $"bytes */{size}";
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            return;
        }

        if (present)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{size}";
            context.Response.ContentLength = to - from + 1;
            if (body)
            {
                await using var s = await storage.GetObjectRangeAsync(key, from, to, context.RequestAborted);
                await s.CopyToAsync(context.Response.Body, context.RequestAborted);
            }

            return;
        }

        context.Response.ContentLength = size;
        if (body)
        {
            await using var s = await storage.GetObjectAsync(key, context.RequestAborted);
            await s.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    // Parses the first byte range of a `Range: bytes=…` header against the object size. Returns Present=false
    // when there's no range header; Ok=false for a malformed / unsatisfiable range.
    private static (bool Present, bool Ok, long From, long To) ParseRange(string header, long size)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, 0, 0);
        }

        var spec = header["bytes=".Length..].Split(',')[0].Trim(); // first range only
        var dash = spec.IndexOf('-');
        if (dash < 0 || size <= 0)
        {
            return (true, false, 0, 0);
        }

        var startStr = spec[..dash];
        var endStr = spec[(dash + 1)..];
        long from, to;
        if (startStr.Length == 0)
        {
            if (!long.TryParse(endStr, out var suffix) || suffix <= 0)
            {
                return (true, false, 0, 0);
            }

            from = Math.Max(0, size - suffix);
            to = size - 1;
        }
        else
        {
            if (!long.TryParse(startStr, out from))
            {
                return (true, false, 0, 0);
            }

            to = endStr.Length == 0 ? size - 1 : long.TryParse(endStr, out var e) ? e : -1;
            if (to < 0)
            {
                return (true, false, 0, 0);
            }
        }

        to = Math.Min(to, size - 1);
        return from > to || from >= size ? (true, false, 0, 0) : (true, true, from, to);
    }

    // Advertise write-lock capability on every resource (exclusive + shared write locks). The server already sends
    // DAV: 1, 2 on OPTIONS, but lock-checking office editors read the per-resource
    // <D:supportedlock> property in PROPFIND to decide a file is writable — without it they open read-only even
    // though a LOCK would succeed. (ADR 0508 WebDAV atomic-save; the LOCK/UNLOCK handlers back a real lock store.)
    private const string SupportedLockXml =
        "<D:supportedlock>" +
        "<D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>" +
        "<D:lockentry><D:lockscope><D:shared/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>" +
        "</D:supportedlock>";

    /// <summary>The PROPFIND properties of a virtual collection — an Intray/Check-out folder or a safe-save scratch.</summary>
    /// <remarks>
    /// <paramref name="modified"/> is REQUIRED rather than defaulted, because the bug it fixes was a default.
    /// This method hardcoded the UNIX EPOCH, so every special folder told its client it had not changed since
    /// 1970 while the items inside it carried today's date. With no ETag anywhere in this gateway, a collection's
    /// mtime is the ONLY cache validator a client has — so a frozen one is not a cosmetic wrong value, it is the
    /// server permanently asserting "nothing new here" to a client asking whether to re-read (#794). Use
    /// <see cref="NewestOf"/> to derive it from the members.
    /// </remarks>
    internal static PropStatXml CollectionProp(string basePath, List<string> segments, string displayName, DateTimeOffset modified, DateTimeOffset? created = null)
    {
        var props = new StringBuilder();
        props.Append($"<D:displayname>{WebDavXml.Xml(displayName)}</D:displayname>");
        props.Append("<D:resourcetype><D:collection/></D:resourcetype>");
        props.Append($"<D:getlastmodified>{modified.ToString("R", CultureInfo.InvariantCulture)}</D:getlastmodified>");
        props.Append($"<D:creationdate>{(created ?? modified).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</D:creationdate>");
        props.Append(SupportedLockXml);
        return new PropStatXml(WebDavPathResolver.HrefFor(basePath, segments) + "/", "HTTP/1.1 200 OK", props.ToString());
    }

    /// <summary>A virtual collection's modified time: the newest thing in it.</summary>
    /// <remarks>
    /// An EMPTY folder answers <c>now</c> rather than a fixed date on purpose. The alternative — falling back to
    /// a constant — makes the mtime jump BACKWARDS when the last item is deleted, which reads to a client as
    /// "older than what you cached", i.e. no change, for the one event it most needs to notice. Revalidating an
    /// empty listing costs nothing, so the harmless lie is preferred to the harmful one.
    /// </remarks>
    internal static DateTimeOffset NewestOf(IEnumerable<DateTimeOffset> memberTimes)
    {
        DateTimeOffset? newest = null;
        foreach (var time in memberTimes)
        {
            newest = newest is null || time > newest ? time : newest;
        }

        return newest ?? DateTimeOffset.UtcNow;
    }

    internal static PropStatXml FileProp(string basePath, List<string> segments, long size, DateTimeOffset modified, string contentType, DateTimeOffset? created = null)
    {
        var props = new StringBuilder();
        props.Append($"<D:displayname>{WebDavXml.Xml(segments[^1])}</D:displayname><D:resourcetype/>");
        props.Append($"<D:getcontentlength>{size}</D:getcontentlength>");
        props.Append($"<D:getcontenttype>{WebDavXml.Xml(contentType)}</D:getcontenttype>");
        props.Append($"<D:getlastmodified>{modified.ToString("R", CultureInfo.InvariantCulture)}</D:getlastmodified>");
        props.Append($"<D:creationdate>{(created ?? modified).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</D:creationdate>");
        props.Append($"<D:getetag>{WebDavXml.Xml(WebDavETag.For(size, modified))}</D:getetag>");
        props.Append(SupportedLockXml);
        return new PropStatXml(WebDavPathResolver.HrefFor(basePath, segments), "HTTP/1.1 200 OK", props.ToString());
    }
}
