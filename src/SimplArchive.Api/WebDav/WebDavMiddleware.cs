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

// The WebDAV gateway (ADR "WebDAV gateway") — mounts the archive as a native OS drive. Hand-rolled over a
// bounded method set (OPTIONS/PROPFIND/GET/HEAD/PUT/MKCOL/DELETE/MOVE/COPY + real LOCK/UNLOCK), mapping
// WebDAV paths onto the Document tree: the root lists the user's Personal repository + the shared repositories
// they can see; a collection is a folder (a Document with no version), a file is a Document with a current
// version (its WebDAV name is the stem + the version's extension). Writes route through the same finalize /
// create-child paths as the API. Auth is HTTP Basic against the per-user app-specific WebDAV password.
public sealed class WebDavMiddleware
{
    // The single mounted resource is served at /SimplArchive so an OS mount (Finder / Explorer / Nautilus) is
    // named "SimplArchive" — the OS takes the volume name from the URL's last path segment, not the DAV
    // displayname (ADR 0509). It is the ONLY path the gateway answers on; hrefs are always emitted under it.
    public const string BasePath = "/SimplArchive";
    // The legacy `/webdav` alias is RETIRED (#794). It existed so mounts predating ADR 0509 kept working, and
    // the cost of keeping it was two ways in: two code paths to reason about, and — the part that actually bit —
    // an entire test suite exercising the alias while every real client used /SimplArchive. One mount, one path.

    // Returns BasePath when the path is the gateway's, else null — one path, so the "which prefix?" question the
    // retired alias used to pose no longer exists.
    /// <summary>
    /// Whether this path is the WebDAV gateway's — asked by the DAV wire trace, which must cover this surface
    /// too (#595). One matcher, so the trace cannot disagree with the gateway about what it serves.
    /// </summary>
    internal static bool IsGatewayPath(string path) => MatchedBase(path) is not null;

    private static string? MatchedBase(string path) =>
        path.Equals(BasePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(BasePath + "/", StringComparison.OrdinalIgnoreCase)
            ? BasePath
            : null;

    // Two special folders nested under the caller's Personal repository (ADR "WebDAV Inbox + Check-out folders",
    // grouped under Personal by ADR "WebDAV Inbox/Check-out under Personal"): the per-user Intray (an S3-backed
    // staging prefix) and Check-out (the caller's checked-out documents + their working-copy stash). Their WebDAV
    // paths are /SimplArchive/Personal/Intray and /SimplArchive/Personal/Check-out — virtual (not Documents),
    // shadowing any real same-named child of Personal.
    internal const string IntrayName = "Intray";
    internal const string CheckoutName = "Check-out";

    // The caller's own personal-space name, stashed per request by the dispatch below (ADR 0671). It USED to be
    // a constant, because every personal space was called "Personal"; now it is whatever its owner is called, so
    // the question "is this the special Intray/Check-out path?" is per-user and cannot be answered by a literal.
    //
    // Taken from the ROOT DOCUMENT rather than recomputed from the display name, and that distinction is
    // load-bearing: the rename is not backfilled, so a space provisioned earlier is still called "Personal" and a
    // check against the sanitised display name would stop finding its Intray.
    private const string PersonalNameKey = "webdav.personalName";

    internal static string PersonalNameFor(HttpContext context) =>
        context.Items[PersonalNameKey] as string ?? PersonalRepositoryProvisioner.LegacyPersonalRepositoryName;

    // True when the path addresses (or sits inside) one of the Personal-nested special folders:
    // [<personal>, Intray|Check-out, file?]. The special file, when present, is segments[2].
    /// <summary>The virtual Intray / Check-out surface — object-storage items, not the Document tree.</summary>
    /// <remarks>
    /// A safe-save scratch collection inside one of those folders is deliberately NOT "special": it is not an
    /// item of that surface, it is an editor's working directory that happens to sit there (#794). Excluding it
    /// here is one line instead of eight, and it means the staging, shadow and lock-null handling the tree
    /// already has serves the Intray unchanged — those are keyed by path and know nothing about Documents.
    ///
    /// Only the COMMIT differs, and it has to: an Intray item is a blob under the user's prefix, so there is no
    /// document to version and no check-out to take.
    /// </remarks>
    internal static bool IsSpecialPath(HttpContext context, List<string> segments) =>
        segments.Count >= 2 && segments[0] == PersonalNameFor(context) && segments[1] is IntrayName or CheckoutName
        && !WebDavClutter.IsSafeSaveScope(segments);

    private readonly RequestDelegate _next;
    private readonly WebDavLockStore _lockStore;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public WebDavMiddleware(RequestDelegate next, WebDavLockStore lockStore)
    {
        _next = next;
        _lockStore = lockStore;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var matchedBase = MatchedBase(path);
        if (matchedBase is null)
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method.ToUpperInvariant();

        var services = context.RequestServices;
        var db = services.GetRequiredService<SimplArchiveDbContext>();

        // The whole exchange, at Trace (ADR 0626). Off everywhere by default; the point is that it EXISTS, so
        // an interop question is one config change away rather than five rounds of inference from status codes.
        //
        // BEFORE authentication, and completed on every exit including a refusal: "the mount is rejected" is
        // precisely the exchange somebody needs to read, and a trace that starts after the gate cannot show it.
        var trace = services.GetRequiredService<ILogger<WebDavMiddleware>>();
        WebDavTrace.Request(trace, context, method, path);

        // The XML verbs carry their meaning in the body, so at Trace it is read and REWOUND for the handler.
        // Buffering is enabled only for those verbs — never for PUT, whose body is the user's document and is
        // both unlogged and potentially enormous.
        if (WebDavTrace.TracesBody(trace, method))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            WebDavTrace.RequestBody(trace, method, path, await reader.ReadToEndAsync(context.RequestAborted));
            context.Request.Body.Position = 0;
        }

        context.Response.OnStarting(() =>
        {
            WebDavTrace.Response(trace, context, method, path);
            return Task.CompletedTask;
        });

        // ---- Basic auth against the app-specific WebDAV password -------------------------------------------
        var user = await AuthenticateAsync(context, db);
        if (user is null)
        {
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"SimplArchive WebDAV\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Make the tenant/user visible to the DbContext query filter + the effective-rights calculator.
        services.GetRequiredService<CurrentTenantAccessor>().TenantId = user.TenantId;
        services.GetRequiredService<CurrentUserAccessor>().UserId = user.Id;

        // The Personal repository is the home for the Intray / Check-out folders and always appears at the WebDAV
        // root — ensure it exists (get-or-create, idempotent) before serving any request.
        var personalRoot = await services.GetRequiredService<PersonalRepositoryProvisioner>().EnsureAsync(user.Id, user.TenantId, context.RequestAborted);
        context.Items[PersonalNameKey] = personalRoot.Name;

        var segments = path[matchedBase.Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();

        // A mount saved before the #795 heal still addresses the space as "Personal" — canonicalise the alias
        // to the owner name — the same recipe as the /webdav → /SimplArchive move: the canonical name changes and
        // the old one is served as an alias. (That particular alias was later retired outright, #794; the recipe
        // is the precedent here, not a claim that /webdav still routes.)
        // Guarded twice: a space genuinely still named "Personal" needs no alias, and a REAL shared root that
        // happens to be called "Personal" must keep winning over the alias — an alias that shadows a real name
        // serves the wrong archive, which is worse than a broken mount.
        if (segments.Count > 0
            && segments[0] == PersonalRepositoryProvisioner.LegacyPersonalRepositoryName
            && personalRoot.Name != PersonalRepositoryProvisioner.LegacyPersonalRepositoryName
            && !await db.Documents.AnyAsync(d =>
                d.ParentId == null && d.PersonalOfUserId == null
                && d.Name == PersonalRepositoryProvisioner.LegacyPersonalRepositoryName))
        {
            segments[0] = personalRoot.Name;
        }

        switch (method)
        {
            case "OPTIONS": HandleOptions(context); break;
            case "PROPFIND": await HandlePropFindAsync(context, services, db, user, segments, matchedBase); break;
            case "GET": await HandleGetAsync(context, services, db, user, segments, body: true); break;
            case "HEAD": await HandleGetAsync(context, services, db, user, segments, body: false); break;
            case "PUT": await HandlePutAsync(context, services, db, user, segments); break;
            case "MKCOL": await HandleMkColAsync(context, services, db, user, segments); break;
            case "DELETE": await HandleDeleteAsync(context, services, db, user, segments); break;
            case "MOVE": await HandleMoveAsync(context, services, db, user, segments); break;
            case "COPY": await HandleCopyAsync(context, services, db, user, segments); break;
            case "LOCK": await WebDavLockHandling.HandleLockAsync(_lockStore, services, context, user, segments); break;
            case "UNLOCK": WebDavLockHandling.HandleUnlock(_lockStore, context, user, segments); break;
            case "PROPPATCH": await HandlePropPatchAsync(context, db, user, segments, matchedBase); break;
            default: context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; break;
        }

    }

    private async Task<User?> AuthenticateAsync(HttpContext context, SimplArchiveDbContext db)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
        catch (FormatException) { return null; }

        var sep = decoded.IndexOf(':');
        if (sep < 0)
        {
            return null;
        }

        var email = decoded[..sep];
        var password = decoded[(sep + 1)..];
        var normalized = email.ToUpperInvariant();

        // No tenant is known yet — bypass the tenant filter to find the user by email, then rely on the stored
        // hash for verification (the same pattern as the interactive login page).
        var user = await db.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive);
        if (user?.WebDavPasswordHash is null ||
            _passwordHasher.VerifyHashedPassword(user, user.WebDavPasswordHash, password) == PasswordVerificationResult.Failed)
        {
            return null;
        }

        return user;
    }

    private static void HandleOptions(HttpContext context)
    {
        context.Response.Headers["DAV"] = "1, 2";
        context.Response.Headers["Allow"] = "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, LOCK, UNLOCK";
        context.Response.Headers["MS-Author-Via"] = "DAV"; // Windows mini-redirector
        context.Response.StatusCode = StatusCodes.Status200OK;
    }










    private static Task HandlePropPatchAsync(HttpContext context, SimplArchiveDbContext db, User user, List<string> segments, string basePath) =>
        // We store no dead properties; accept the request as a no-op success so clients (esp. Finder setting
        // timestamps) don't fail the copy.
        WebDavXml.WriteMultiStatusAsync(context, [new PropStatXml(WebDavPathResolver.HrefFor(basePath, segments), "HTTP/1.1 200 OK", "")]);

    // ---- PROPFIND ------------------------------------------------------------------------------------------
    private async Task HandlePropFindAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, string basePath)
    {
        var depth = context.Request.Headers["Depth"].ToString();
        depth = string.IsNullOrEmpty(depth) ? "1" : depth; // some clients omit Depth; default 1

        // The special Personal/Intray and Personal/Check-out folders are backed by object storage / the check-out
        // entity, not the Document tree (ADR "WebDAV Inbox + Check-out folders").
        if (IsSpecialPath(context, segments))
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
    private async Task HandleGetAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, bool body)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();

        // Staged aside by an atomic save (#794) — the same answer PROPFIND gives, because a path cannot be
        // missing to one verb and present to another. The bytes are at the backup path the editor moved them to.
        if (!IsSpecialPath(context, segments)
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
        if (segments.Count > 0 && !IsSpecialPath(context, segments))
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

        if (IsSpecialPath(context, segments))
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

    // ---- PUT (create or new version) ----------------------------------------------------------------------
    private async Task HandlePutAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialPutAsync(context, services, db, user, segments);
            return;
        }

        // Writing to a path that was staged aside puts it back, by whatever route (#794). The set-aside is a
        // claim about the mount, not about the archive, so anything that makes the path real again retires it.
        if (segments.Count > 0)
        {
            await WebDavSafeSave.ClearSetAsideAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted);
        }

        // OS clutter (._*, .DS_Store, Thumbs.db, …) never becomes a document (ADR "WebDAV clutter filter") — but
        // it is REMEMBERED rather than dropped. Accepting a write and then answering 404 to a read of it is the
        // defect that produced "Word cannot complete the save due to a file permission error": measured on the
        // wire, the editor wrote `._<name>`, got 201, asked for it, got 404, wrote it AGAIN (201, not 204 —
        // proof nothing was kept), and eventually concluded it could not write at all.
        //
        // ABOVE the root refusal below, which is the whole point of it being here rather than with its siblings
        // further down (#794). Finder drops a `.DS_Store` in every directory it displays, the MOUNT ROOT
        // included, and there the root guard ran first: the same file was accepted one level down and refused
        // with 403 at the top. That 403 was the ONLY non-2xx in a ninety-second trace of a failing save, and
        // what a refusal at the root tells macOS is not "not that file" but "this volume does not take writes"
        // — after which the editor stopped attempting an atomic replace at all, opening scratch collection
        // after scratch collection and never writing the document into any of them.
        //
        // A rule enforced at one entrance is not a rule. This one is stated for the whole mount, so it is
        // applied before anything narrows the path down.
        // NOT inside a safe-save collection, which is the exception this hoist has to carry with it. A `._`
        // sidecar written into a scratch collection belongs to the COLLECTION's staging area, and LOCK and GET
        // both look for it there (`IsUnderSafeSaveTemp ? FileKey : ShadowKey`). Sending the PUT to the shadow
        // area instead splits one path across two keys, and the wire says so precisely: `PUT … → 201` followed
        // by `LOCK … → 201` — a 201 from LOCK means the resource did not exist, contradicting the PUT that had
        // just made it, after which the editor rewrote the same 4 KB sidecar four times and gave up. The
        // ordering that was here before put the safe-save branch first for exactly this reason.
        if (segments.Count > 0
            && !WebDavClutter.IsUnderSafeSaveTemp(segments)
            && WebDavClutter.IsOsClutter(segments[^1]))
        {
            await WebDavSpecialHandlers.StageShadowAsync(context, services, user, segments);
            return;
        }

        if (segments.Count < 2)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't PUT at the repository-list root
            return;
        }

        if (WebDavLockHandling.IsLocked(_lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var fileName = segments[^1];

        // A write INSIDE a safe-save collection (#762). Answering 201 to the MKCOL was a PROMISE, and this is
        // the verb that has to keep it: the collection was never materialised, so resolving this path's parent
        // fails and the editor got a 409 — "failed to write" — with the original left untouched but unsaved.
        //
        // Staged in the same per-user scratch the save-by-rename flow uses (ADR 0562), keyed by the LEAF name.
        // That is not a coincidence to lean on but the shape of the thing: the editor's next step renames this
        // file over the original, and inside a safe-save collection the leaf already HAS the original's exact
        // name — so TryCommitImplicitCheckoutAsync finds it on the MOVE and commits it, unchanged, with no new
        // code on the commit side at all.
        //
        // Accept-and-discard, which is right for junk nobody writes into (.DS_Store, .Trashes), was wrong here
        // for exactly one reason: a safe-save collection is a directory the editor DOES write into.
        if (WebDavClutter.IsUnderSafeSaveTemp(segments))
        {
            await WebDavSpecialHandlers.StageSafeSaveAsync(context, services, user, segments);
            return;
        }

        // A browser in-progress download (.crdownload/.part/.partial/.dltemp) is STAGED in the per-user temp
        // area (not dropped, not materialized as a document) and committed to a real document on the completing
        // MOVE (ADR "WebDAV .crdownload staging"). Checked before the clutter filter, which also matches these.
        if (WebDavClutter.IsDownloadTemp(fileName))
        {
            await WebDavSpecialHandlers.StageDownloadTempAsync(context, services, db, user, segments);
            return;
        }

        // An editor temp / owner sidecar (~$*, .tmp, .swp, …) is BUFFERED in the per-user scratch area rather
        // than discarded (ADR 0562). Discarding it is what made editing in place fail for a suite that saves by
        // rename: it writes the new content to a temporary name and then renames that over the original, so a
        // discarded temp leaves the committing MOVE with no source. The Check-out folder has buffered these
        // since ADR 0508; this is the same thing one level out, in the tree where the document actually lives.
        //
        // Still never a document: nothing here creates or names a row, the scratch prefix is outside the mounted
        // structure (ADR 0509), and an abandoned buffer is just an orphan object, exactly as under Check-out.
        if (WebDavClutter.IsTransientClutter(fileName))
        {
            await WebDavSpecialHandlers.StageTreeScratchAsync(context, services, fileName, user);
            return;
        }

        var parent = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // parent missing / not a collection
            return;
        }

        var rights = await RightsAsync(services, user, parentDoc.Id);
        var existing = await WebDavPathResolver.ResolveAsync(db, user, segments);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // A LOCK/create dance from Finder sends a 0-byte PUT first; buffer the body to a temp object either way.
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var finalizer = services.GetRequiredService<DocumentFinalizer>();
        var now = DateTimeOffset.UtcNow;
        // The key groups by the document (ADR 0530): an existing document reuses its filing year + storage folder;
        // a new document gets `now` + a fresh storage folder. The version id is the leaf either way, generated up
        // front so the DocumentVersion below reuses it.
        var versionId = Guid.NewGuid();
        DateTimeOffset keyYear;
        Guid keyStorageFolderId;
        if (existing is { Document: { } existingDoc, IsCollection: false })
        {
            keyYear = existingDoc.CreatedAt;
            keyStorageFolderId = existingDoc.StorageFolderId;
        }
        else
        {
            keyYear = now;
            keyStorageFolderId = Guid.NewGuid();
        }
        var objectKey = ObjectKeyBuilder.Build(user.TenantId, keyYear, keyStorageFolderId, versionId, extension);
        // Buffer the body so object storage has a known content length (the request stream may be
        // chunked / non-seekable).
        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;

        // A zero-byte PUT to a NEW name is the OS creating the file before it writes to it — macOS does exactly
        // this, and a browser does the same while streaming into a sibling .crdownload. It becomes a REAL, empty
        // document, and that is a deliberate reversal: it used to be accepted and discarded.
        //
        // Discarding it broke atomic saving invisibly. The file macOS had just created answered 404 on the next
        // read, so the editor wrote its content into the scratch collection, went to swap it over the original,
        // found no original, and abandoned — never issuing the MOVE. Measured (#762): PUT Test987.docx 0B → 201,
        // PUT …/.~WRD3576 13471B → 204, DELETE …/.~WRD3576, and no MOVE anywhere. Word reported success, because
        // webdavfs had been told 201 and had no reason to doubt it.
        //
        // Making it visible-but-not-a-document was tried next and moved the same contradiction one verb along:
        // PROPFIND said it existed, GET said 404, and macOS deleted the file it could not read. A placeholder
        // has to be a REAL resource or every verb has to learn about it separately — and a created-but-unwritten
        // file is an empty file on any filesystem, so an empty document is the honest representation. The
        // content that follows becomes a new VERSION of it, which is the check-out semantics we already want.


        // A write over a document that ALREADY HAS CONTENT is a working copy, not a version — whatever route the
        // application took to get here. Some editors save through a scratch collection and a swap; others, an
        // office spreadsheet among them, simply PUT the whole file at its real name:
        //
        //     PUT …/Contoso Cloud/Book1.xlsx  Content-Length: 8910   (no collection, no MOVE)
        //
        // Both are the same act. Routing only the first to a check-out made the archive's behaviour depend on
        // WHICH APPLICATION you saved from, with nothing in the UI to explain why one app's edits need checking
        // in and another's did not — an inconsistency worse than either rule alone.
        //
        // A PUT to a NEW name still creates a document: that is filing something, not editing it.
        // …but NOT a zero-byte one. macOS opens a file for writing by creating/truncating it first and sends the
        // content in a second request, so an empty body here is the OS clearing its throat, not an edit.
        // Treating it as one stashed an EMPTY working copy over a document that had content — and since the
        // tree serves the owner their working copy, the file then read as 0 bytes while v2 sat in the archive
        // holding all 13311 of them. Nothing was lost, and it looked exactly like loss, which is nearly as bad.
        if (buffered.Length > 0
            && existing is { Document: { } target, IsCollection: false }
            && await db.DocumentVersions.AnyAsync(
                v => v.DocumentId == target.Id && v.Status == DocumentVersionStatus.Confirmed && v.SizeBytes > 0,
                context.RequestAborted))
        {
            await WebDavSpecialHandlers.StashOverExistingAsync(context, services, db, user, target, buffered);
            return;
        }

        // The OS's create/truncate against a document that already has content: accepted, and it changes
        // nothing. The content arrives in the next request.
        if (buffered.Length == 0 && existing is { IsCollection: false })
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Enforce the tenant storage quota (ADR "WebDAV hardening" / ADR "Per-tenant storage quota") before the
        // blob is committed — return 507 Insufficient Storage (the code WebDAV clients understand).
        if (!await services.GetRequiredService<IStorageQuotaService>().CanStoreAsync(user.TenantId, buffered.Length, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            return;
        }

        await storage.PutObjectAsync(objectKey, buffered, context.Request.ContentType ?? "application/octet-stream", context.RequestAborted);

        Document document;
        if (existing is { Document: { } doc, IsCollection: false })
        {
            if (!rights.CanEditContent) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            document = doc;
        }
        else if (existing is not null)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; // a collection already exists here
            return;
        }
        else
        {
            if (!rights.CanCreateSubItems) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

            // Whether the destination admits only its own listed masks (a Calendar, an Addressbook, a Notebook).
            var parentMaskId = await db.MaskVersions.Where(mv => mv.Id == parentDoc.MaskVersionId)
                .Select(mv => (Guid?)mv.MaskId).FirstOrDefaultAsync(context.RequestAborted);
            var parentIsTypedFolder = parentMaskId is { } pm
                && WellKnownMaskIds.TypedFolderRules.Any(r => r.FolderMaskId == pm);

            // Stamped with the Folder mask at creation, exactly as the API's create does — the finalizer
            // reclassifies it to Basic Entry / eMail once the bytes arrive (ADR "Folder mask on folders").
            //
            // Creating it MASKLESS is what let a file dropped on the mounted `Personal` drive land at the
            // personal space's first level (#644): maskless is admitted there (it is the pre-upgrade state),
            // and the rule is gated on arrival, so the finalizer's later stamp was never re-checked. Stamping
            // here refuses it at creation, BEFORE any bytes transfer, which is what the API and both clients
            // already do (ADR 0637).
            document = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                ParentId = parentDoc.Id,
                Name = stem,
                // …but NULL inside a TYPED folder, which is the other half of the API's rule and the half that
                // matters here: a My Calendar / My Addressbook admits only Appointments / Contacts, so a
                // Folder-masked child is refused outright. Those uploads must arrive unclassified and let the
                // finalizer decide what they are — which is exactly how a .ics or .vcf becomes one.
                MaskVersionId = parentIsTypedFolder
                    ? null
                    : await FolderMask.CurrentVersionIdAsync(db, user.TenantId, WellKnownMaskIds.Folder, context.RequestAborted)
                        ?? await FolderMask.CurrentVersionIdAsync(db, context.RequestAborted),
                CreatedByUserId = user.Id,
                CreatedAt = now,
                StorageFolderId = keyStorageFolderId,
            };
            db.Documents.Add(document);
            try { await db.SaveChangesAsync(context.RequestAborted); }
            catch (SimplArchive.Domain.Documents.PersonalSpaceStructureException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            catch (Domain.Masks.TypedFolderContainmentException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; } // sibling-name clash
        }

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = user.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = user.Id,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        db.DocumentVersions.Add(version);
        await db.SaveChangesAsync(context.RequestAborted);
        await finalizer.FinalizeAsync(version, context.RequestAborted);

        context.Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }



    // ---- MKCOL (create folder) ----------------------------------------------------------------------------
    private async Task HandleMkColAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        // BEFORE the special-path refusal, and that ordering is the fix (#764). A word processor's atomic replace
        // creates a `<file>.sb-<hex>-<rand>` collection; refusing it made the editor roll back and DELETE THE
        // ORIGINAL — in the Intray, where items have no soft-delete, unrecoverably. Accepted and discarded, like
        // any other junk directory: the editor gets its "yes", and nothing is materialised.
        if (segments.Count >= 2 && WebDavClutter.IsSafeSaveTemp(segments[^1]))
        {
            // RECORDED, not discarded. Discarding is what made every later verb a lie: the editor went on to
            // PUT into a collection we said did not exist, and to PROPFIND files we had already accepted.
            await WebDavSafeSave.CreateAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // Silently accept OS-junk directories (.Trashes, .TemporaryItems, .fseventsd, .Spotlight-V100 …) without
        // creating a folder document (ADR "WebDAV clutter filter"). Before the root refusal, for the reason
        // given on PUT: macOS creates these AT THE MOUNT ROOT, and refusing them there says the volume is
        // read-only rather than saying no to one directory (#794).
        if (segments.Count > 0 && WebDavClutter.IsOsClutter(segments[^1]))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        if (segments.Count < 2 || IsSpecialPath(context, segments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't create folders at the root, on the virtual Intray/Check-out folders, or inside them
            return;
        }

        if (WebDavLockHandling.IsLocked(_lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var parent = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (await WebDavPathResolver.ResolveAsync(db, user, segments) is not null)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; // already exists
            return;
        }

        var rights = await RightsAsync(context.RequestServices, user, parentDoc.Id);
        if (!rights.CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Assign the Folder mask like every other folder-creation path (ADR "Folder mask on folders").
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            ParentId = parentDoc.Id,
            Name = segments[^1],
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, context.RequestAborted),
            CreatedByUserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }

        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    // ---- DELETE (soft-delete to the recycle bin) ----------------------------------------------------------
    private async Task HandleDeleteAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        // The safe-save collection, or anything inside it (#762). It was never materialised, so there is nothing
        // to delete — but 404 is the wrong answer to give an editor tidying up after a save it believes
        // succeeded, and it is the answer this path used to give. Same promise as the 201: having accepted the
        // collection, every later verb has to behave as though it exists.
        if (WebDavClutter.IsSafeSaveScope(segments))
        {
            await WebDavSafeSave.RemoveAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (IsSpecialPath(context, segments))
        {
            if (segments.Count != 3)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var storage = services.GetRequiredService<IObjectStorageClient>();
            var name = segments[2];

            if (segments[1] == IntrayName)
            {
                // A remembered write is deletable. Answering 404 for something we accepted with 201 is the same
                // contradiction in its last place on this surface (#794) — the editor deletes its sidecar as
                // part of tidying up, and a 404 there reads as the save having gone wrong.
                if (WebDavClutter.IsOsClutter(name))
                {
                    var shadowKey = WebDavSafeSave.ShadowKey(user, segments);
                    if (await storage.ExistsAsync(shadowKey, context.RequestAborted))
                    {
                        await storage.DeleteObjectAsync(shadowKey, context.RequestAborted);
                    }

                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                if ((await WebDavUserAreas.IntrayFilesAsync(storage, user)).All(f => f.Name != name) && !WebDavClutter.IsLockFile(name))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await storage.DeleteObjectAsync(WebDavUserAreas.IntrayPrefix(user) + name, context.RequestAborted);
                try { await storage.DeleteObjectAsync(WebDavUserAreas.IntrayPrefix(user) + name + ".mask.json", context.RequestAborted); } catch (Exception) { /* sidecar may not exist */ }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // Check-out (ADR 0508): deleting a checked-out doc's name is the editor's pre-rename delete — a no-op
            // (the check-out is released only via the client); deleting a scratch temp/lock file removes it.
            if ((await WebDavUserAreas.CheckoutFilesAsync(storage, db, user)).Any(f => f.Name == name))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent; // no-op: keep the check-out
                return;
            }

            var scratchKey = WebDavUserAreas.CheckoutScratchPrefix(user) + name;
            if (await storage.ExistsAsync(scratchKey, context.RequestAborted))
            {
                await storage.DeleteObjectAsync(scratchKey, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = WebDavClutter.IsLockFile(name) ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
            return;
        }

        // A browser cancelling/finishing a download deletes its in-progress temp file; drop any staged blob and
        // succeed (there is no Document to remove). ADR "WebDAV .crdownload staging".
        if (WebDavClutter.IsDownloadTemp(segments[^1]))
        {
            var storage = services.GetRequiredService<IObjectStorageClient>();
            var tempKey = WebDavUserAreas.TempKeyFor(user, segments);
            if (await storage.ExistsAsync(tempKey, context.RequestAborted))
            {
                await storage.DeleteObjectAsync(tempKey, context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (WebDavLockHandling.IsLocked(_lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (node?.Document is not { } document)
        {
            // A remembered write is deletable, ANYWHERE — the Intray path below has said so since #794 and the
            // tree had not, so a `.DS_Store` we accepted at the mount root could not be removed again. Accepting
            // a write is a promise every later verb has to keep (ADR 0707), and DELETE is a later verb: the OS
            // tidies its own junk, and a 404 for something it just wrote reads as the volume losing writes.
            if (segments.Count > 0 && (WebDavClutter.IsOsClutter(segments[^1]) || WebDavClutter.IsTransientClutter(segments[^1])))
            {
                var clutterStorage = services.GetRequiredService<IObjectStorageClient>();

                // The SAME key selector PUT, GET and LOCK use. A `._` sidecar inside a scratch collection is
                // staged with the collection, not in the shadow area, and deleting the shadow key instead
                // answers 204 while the file it claimed to remove is still there (#794). A transient name
                // (`~$…`, editor temps) lives in the tree scratch — refusing its DELETE with 404 is what left
                // the editor's own lock file behind after it closed, unable to clean up after itself; the
                // legacy shadow a pre-fix LOCK wrote for the same name is swept in the same breath.
                var clutterKey = WebDavClutter.IsUnderSafeSaveTemp(segments)
                    ? WebDavSafeSave.FileKey(user, segments)
                    : WebDavClutter.IsTransientClutter(segments[^1])
                        ? WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1]
                        : WebDavSafeSave.ShadowKey(user, segments);
                if (await clutterStorage.ExistsAsync(clutterKey, context.RequestAborted))
                {
                    await clutterStorage.DeleteObjectAsync(clutterKey, context.RequestAborted);
                }

                var legacyShadow = WebDavSafeSave.ShadowKey(user, segments);
                if (legacyShadow != clutterKey && await clutterStorage.ExistsAsync(legacyShadow, context.RequestAborted))
                {
                    await clutterStorage.DeleteObjectAsync(legacyShadow, context.RequestAborted);
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = node is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden; // can't delete a virtual root/repository listing
            return;
        }

        // Reached through a REFERENCE: delete the APPEARANCE, never the document (#769). This is the one that
        // loses data if guessed wrong — a user tidying a working folder on a mounted drive would otherwise
        // destroy the document itself, which is still filed somewhere they were not looking.
        //
        // Gated on the FOLDER's right, not the target's, for the same reason the API gates it there: removing
        // a shortcut changes the contents of the folder holding it and nothing about the document.
        if (node.ViaReferenceId is { } referenceId)
        {
            var folder = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
            if (folder?.Document is not { } holder || !(await RightsAsync(services, user, holder.Id)).CanCreateSubItems)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var reference = await db.DocumentReferences.FirstOrDefaultAsync(r => r.Id == referenceId, context.RequestAborted);
            if (reference is not null)
            {
                db.DocumentReferences.Remove(reference);
                await db.SaveChangesAsync(context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!(await RightsAsync(services, user, document.Id)).CanDelete)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Soft-delete the subtree (the same cascade as the API's DELETE).
        foreach (var d in await WebDavPathResolver.CollectSubtreeAsync(db, document.Id))
        {
            d.DeletedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // ---- MOVE (reparent + rename) -------------------------------------------------------------------------
    private async Task HandleMoveAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        // An atomic save in the INTRAY, whose two halves both cross the special-path boundary (#794). Handled
        // ahead of the divert below, because the source or the destination is an ordinary Intray item while the
        // other side lives in the scratch collection — and the flat-folder handler refuses anything deeper.
        if (await WebDavSpecialHandlers.TryIntraySafeSaveMoveAsync(context, services, db, user, segments))
        {
            return;
        }

        if (IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialRenameAsync(context, services, db, user, segments, keepSource: false); // atomic-save rename within Intray/Check-out (ADR 0508)
            return;
        }

        if (WebDavLockHandling.IsLocked(_lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        // Commit-on-rename: a browser finishing a download renames its in-progress temp (X.crdownload) to the
        // final name — if the source is a staged download-temp, materialize the real document from the staged
        // bytes (ADR "WebDAV .crdownload staging"). Falls through to a normal move when nothing is staged.
        if (WebDavClutter.IsDownloadTemp(segments[^1]) && await WebDavSpecialHandlers.TryCommitDownloadTempAsync(context, services, db, user, segments))
        {
            return;
        }

        // The safe-save swap. The staged file lives under the collection's own area, so its bytes are bridged
        // into the scratch key the implicit-checkout commit already looks for — one commit path serves both
        // atomic-save shapes, rather than a second one that would drift from it.
        //
        // Only when something is actually staged; otherwise this falls through and an ordinary move happens.
        // The SET-ASIDE, which is how macOS actually begins an atomic replace: it moves the ORIGINAL into the
        // scratch collection as a backup (`…/~WRL0328`) and then renames the new content into the vacated name.
        // Measured (#762) — and the opposite direction from the one this code was built for:
        //
        //     MOVE /…/Test_xxx.docx  Destination: /…/Test_xxx.docx.sb-…-KXPK2o/~WRL0328  → 409
        //     DELETE /…/Test_xxx.docx                                                    → 204
        //
        // The 409 came from the destination's parent not being a real folder, and the line after it is the cost:
        // refused the backup, macOS concluded the save had failed and DELETED THE FILE. "The file disappeared."
        //
        // Accepted as a no-op that KEEPS the document where it is. There is nothing to back up — a replace here
        // becomes a new version, and the previous bytes stay reachable through version history, which is a
        // better backup than a copy in a temp folder. The name is remembered so a later read of it answers.
        if (ParseDestination(context) is { Count: > 0 } setAside && WebDavClutter.IsUnderSafeSaveTemp(setAside)
            && !WebDavClutter.IsUnderSafeSaveTemp(segments))
        {
            var asideStorage = services.GetRequiredService<IObjectStorageClient>();
            var asideKey = WebDavSafeSave.FileKey(user, setAside);
            var source = await WebDavPathResolver.ResolveAsync(db, user, segments);

            // The backup must CONTAIN the document, not merely exist. Writing an empty marker here was fine for
            // a new file — the placeholder was empty anyway — and fatal for editing one in place: Word reads its
            // backup back before continuing, and measured (#762) it got `200, Content-Length: 0`, so it stopped
            // rather than destroy the original. That refusal is correct of it and the empty backup was wrong of
            // us. The document itself still stays where it is; version history is our backup, and this copy is
            // the one the editor insists on seeing.
            if (source is { IsCollection: false, ObjectKey: { } sourceKey })
            {
                var held = source.Document is { CheckedOutByUserId: { } by } && by == user.Id
                    ? CheckoutStashKey.Build(user.TenantId, user.Id, source.Document.Id)
                    : null;
                var from = held is not null && await asideStorage.ExistsAsync(held, context.RequestAborted) ? held : sourceKey;
                await asideStorage.CopyObjectAsync(from, asideKey, context.RequestAborted);
            }
            else
            {
                await asideStorage.PutObjectAsync(asideKey, new MemoryStream([]), "application/octet-stream", context.RequestAborted);
            }

            // The move is now ANSWERED, not merely accepted: the document keeps its row and its place in the
            // archive, and the mount reports the path it left as gone until the swap puts something back there
            // (#794). Before this, 201 was a claim nothing upheld — the editor retitled its own window to the
            // backup name and then stopped writing, because the name it had just freed was still occupied.
            await WebDavSafeSave.MarkSetAsideAsync(asideStorage, user, segments, context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // The safe-save swap. The staged bytes are bridged into the keys the two EXISTING commit helpers look
        // for, so neither's logic is duplicated here — one of them serves a REPLACE (implicit check-out onto an
        // existing document, ADR 0562) and the other a CREATE.
        //
        // Both are needed, and only the first was here: TryCommitImplicitCheckoutAsync declines a destination
        // that does not exist ("an ordinary create, handled elsewhere"), and elsewhere resolves the SOURCE as a
        // Document — which a staged file is not. So saving a NEW document through an atomic save had no commit
        // path at all, which is what the wire showed: the target PROPFINDed 404 because it had never existed.
        if (WebDavClutter.IsUnderSafeSaveTemp(segments))
        {
            var safeSaveStorage = services.GetRequiredService<IObjectStorageClient>();
            var stagedKey = WebDavSafeSave.FileKey(user, segments);

            // A sidecar leaving the collection stays OUT of the archive. macOS moves its AppleDouble to the
            // final name alongside the document (`…/.sb-…/._.~WRD3471` → `…/._Test.docx`), and committing THAT
            // minted documents called `._ahfsjishaijf`, `._Line1`, `._The real test` — 4 KB of resource-fork
            // metadata filed as though it were someone's work.
            //
            // The clutter filter decides what may become a document, and it has to decide that on EVERY path in,
            // not just on PUT. A rule enforced at one entrance is not a rule.
            var movedDestination = ParseDestination(context);
            if (movedDestination is { Count: > 0 } && WebDavClutter.IsOsClutter(movedDestination[^1]))
            {
                if (await safeSaveStorage.ExistsAsync(stagedKey, context.RequestAborted))
                {
                    await safeSaveStorage.CopyObjectAsync(
                        stagedKey, WebDavSafeSave.ShadowKey(user, movedDestination), context.RequestAborted);
                    await safeSaveStorage.DeleteObjectAsync(stagedKey, context.RequestAborted);
                }

                context.Response.StatusCode = StatusCodes.Status201Created;
                return;
            }

            // The swap is the other half of the set-aside: something is being put back at the name it emptied,
            // so that name exists again (#794). Cleared before the commit rather than after, so the path is
            // never briefly hidden while it is being written.
            if (movedDestination is { Count: > 0 })
            {
                await WebDavSafeSave.ClearSetAsideAsync(safeSaveStorage, user, movedDestination, context.RequestAborted);
            }

            if (await safeSaveStorage.ExistsAsync(stagedKey, context.RequestAborted))
            {
                await safeSaveStorage.CopyObjectAsync(
                    stagedKey, WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1], context.RequestAborted);
                await safeSaveStorage.CopyObjectAsync(
                    stagedKey, WebDavUserAreas.TempKeyFor(user, segments), context.RequestAborted);
                await safeSaveStorage.DeleteObjectAsync(stagedKey, context.RequestAborted);

                // EVERY save over WebDAV is a working copy, never a silent version (ADR 0562, reaffirmed for
                // #762). The bytes land in the stash, the document shows on the Check-out tab, and check-in is
                // the deliberate act that mints the next version — including the FIRST save of a newly created
                // document, so the rule has no special case to remember.
                //
                // What makes that honest rather than lossy is that the tree SERVES the stash to its owner (see
                // HandleGetAsync): you read back what you saved. Without that half, this half reads as data
                // loss — the file returns the empty placeholder and the editor appears to have thrown your work
                // away.
                if (await WebDavSpecialHandlers.TryCommitImplicitCheckoutAsync(context, services, db, user, segments))
                {
                    await safeSaveStorage.DeleteObjectAsync(WebDavUserAreas.TempKeyFor(user, segments), context.RequestAborted);
                    return;
                }

                if (await WebDavSpecialHandlers.TryCommitDownloadTempAsync(context, services, db, user, segments))
                {
                    await safeSaveStorage.DeleteObjectAsync(
                        WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1], context.RequestAborted);
                    return;
                }
            }
        }

        // Commit-on-rename for a save-by-rename edit: the source is a buffered editor temp and the destination is
        // an existing document, so this rename IS the save. Turns it into an implicit check-out with the bytes in
        // the user's stash (ADR 0562) — never a silent new version, and never a second document beside the first.
        if (await WebDavSpecialHandlers.TryCommitImplicitCheckoutAsync(context, services, db, user, segments))
        {
            return;
        }

        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (node?.Document is not { } document)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (string.IsNullOrEmpty(context.Request.Headers["Destination"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var destSegments = ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || IsSpecialPath(context, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // no/blank destination, the root, or a special folder
            return;
        }

        if (WebDavLockHandling.IsLocked(_lockStore, context, user, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var newName = Path.GetFileNameWithoutExtension(destSegments[^1]);
        var destParent = await WebDavPathResolver.ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        // Dragging a message out of the mounted inbox files it, so the bytes move too (#633). A refused
        // crossing is a 409, the same answer the client gets for any placement this server will not make.
        try
        {
            await services.GetRequiredService<Documents.DocumentMover>()
                .RelocateContentForMoveAsync(document.Id, destParentDoc.Id, context.RequestAborted);
        }
        catch (Errors.Exceptions.Documents.CannotFileIntoEphemeralMailException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        document.Name = newName;
        document.ParentId = destParentDoc.Id;
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }

        context.Response.StatusCode = StatusCodes.Status201Created;
    }


    // Parses the Destination header into WebDAV path segments (null when absent/unparseable).
    internal static List<string>? ParseDestination(HttpContext context)
    {
        var destination = context.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destination))
        {
            return null;
        }

        var uri = new Uri(destination, UriKind.RelativeOrAbsolute);
        var absolute = uri.IsAbsoluteUri ? uri.AbsolutePath : destination;
        var baseIndex = absolute.IndexOf(BasePath, StringComparison.OrdinalIgnoreCase);
        var matchedLength = BasePath.Length;

        var tail = baseIndex >= 0 ? absolute[(baseIndex + matchedLength)..] : absolute;
        return tail.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
    }

    // ---- COPY (duplicate a file or a folder subtree) ------------------------------------------------------
    private async Task HandleCopyAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialRenameAsync(context, services, db, user, segments, keepSource: true); // atomic-save copy within Intray/Check-out (ADR 0508)
            return;
        }

        var source = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (source?.Document is not { } sourceDoc)
        {
            context.Response.StatusCode = source is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
            return;
        }

        if (string.IsNullOrEmpty(context.Request.Headers["Destination"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var destSegments = ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || IsSpecialPath(context, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (WebDavLockHandling.IsLocked(_lockStore, context, user, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        if (!(await RightsAsync(services, user, sourceDoc.Id)).CanReadContent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var destParent = await WebDavPathResolver.ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (!(await RightsAsync(services, user, destParentDoc.Id)).CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Overwrite defaults to true (T); Overwrite: F fails 412 if the destination already exists.
        var overwrite = !context.Request.Headers["Overwrite"].ToString().Equals("F", StringComparison.OrdinalIgnoreCase);
        var existing = await WebDavPathResolver.ResolveAsync(db, user, destSegments);
        if (existing is not null && !overwrite)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        var storage = services.GetRequiredService<IObjectStorageClient>();
        var finalizer = services.GetRequiredService<DocumentFinalizer>();
        try
        {
            await CopyDocumentAsync(db, storage, finalizer, user, sourceDoc.Id, destParentDoc.Id, Path.GetFileNameWithoutExtension(destSegments[^1]), context.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // sibling-name clash
            return;
        }

        context.Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    // Recursively copies a document under destParentId: a file → a new Document + a copy of its current version
    // blob (finalized like an upload); a folder → a new folder + recursed children (keeping their names).
    private static async Task CopyDocumentAsync(SimplArchiveDbContext db, IObjectStorageClient storage, DocumentFinalizer finalizer, User user, Guid sourceId, Guid destParentId, string newName, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Copy the source's current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
        var sourcePointer = await db.Documents.Where(d => d.Id == sourceId).Select(d => d.CurrentVersionId).FirstOrDefaultAsync(ct);
        var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, sourceId, sourcePointer, ct);

        if (version is null)
        {
            var folder = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                ParentId = destParentId,
                Name = newName,
                MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, ct),
                CreatedByUserId = user.Id,
                CreatedAt = now,
            };
            db.Documents.Add(folder);
            await db.SaveChangesAsync(ct);

            var children = await db.Documents.Where(d => d.ParentId == sourceId).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
            foreach (var child in children)
            {
                await CopyDocumentAsync(db, storage, finalizer, user, child.Id, folder.Id, child.Name, ct);
            }

            return;
        }

        // A copy is a brand-new document, so it groups under a fresh storage folder (ADR 0530): `now` + a new
        // storage folder, the new version id as the leaf.
        var storageFolderId = Guid.NewGuid();
        var newVersionId = Guid.NewGuid();
        var newKey = ObjectKeyBuilder.Build(user.TenantId, now, storageFolderId, newVersionId, Path.GetExtension(version.ObjectKey));
        await storage.CopyObjectAsync(version.ObjectKey, newKey, ct);

        var doc = new Document { Id = Guid.NewGuid(), TenantId = user.TenantId, ParentId = destParentId, Name = newName, CreatedByUserId = user.Id, CreatedAt = now, StorageFolderId = storageFolderId };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);

        var newVersion = new DocumentVersion
        {
            Id = newVersionId,
            DocumentId = doc.Id,
            TenantId = user.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = newKey,
            CreatedByUserId = user.Id,
            CreatedAt = now,
            DocumentDate = version.DocumentDate,
        };
        db.DocumentVersions.Add(newVersion);
        await db.SaveChangesAsync(ct);
        await finalizer.FinalizeAsync(newVersion, ct);
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





    internal static async Task<EffectiveRights> RightsAsync(IServiceProvider services, User user, Guid documentId) =>
        await services.GetRequiredService<IEffectiveRightsCalculator>().GetEffectiveRightsAsync(user.Id, documentId);





}
