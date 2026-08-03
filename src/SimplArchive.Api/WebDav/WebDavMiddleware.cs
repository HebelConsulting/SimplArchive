using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.WebDav;

// The WebDAV gateway (ADR "WebDAV gateway") — mounts the archive as a native OS drive. Hand-rolled over a
// bounded method set (OPTIONS/PROPFIND/GET/HEAD/PUT/MKCOL/DELETE/MOVE/COPY + real LOCK/UNLOCK), mapping
// WebDAV paths onto the Document tree: the root lists the user's Personal repository + the shared repositories
// they can see; a collection is a folder (a Document with no version), a file is a Document with a current
// version (its WebDAV name is the stem + the version's extension). Writes route through the same finalize /
// create-child paths as the API. Auth is HTTP Basic against the per-user app-specific WebDAV password.
public sealed partial class WebDavMiddleware
{
    // The single mounted resource is served at /SimplArchive so an OS mount (Finder / Explorer / Nautilus) is
    // named "SimplArchive" — the OS takes the volume name from the URL's last path segment, not the DAV
    // displayname (ADR 0509). The original /webdav path stays accepted for already-saved mounts; hrefs are always
    // emitted under /SimplArchive (the canonical path).
    public const string BasePath = "/SimplArchive";
    private const string LegacyBasePath = "/webdav";

    // The gateway answers at /SimplArchive (canonical) and /webdav (legacy). Returns whichever prefix the request
    // used, or null when the path isn't the gateway's.
    private static string? MatchedBase(string path) =>
        path.Equals(BasePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(BasePath + "/", StringComparison.OrdinalIgnoreCase) ? BasePath
        : path.Equals(LegacyBasePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(LegacyBasePath + "/", StringComparison.OrdinalIgnoreCase) ? LegacyBasePath
        : null;

    // Two special folders nested under the caller's Personal repository (ADR "WebDAV Inbox + Check-out folders",
    // grouped under Personal by ADR "WebDAV Inbox/Check-out under Personal"): the per-user Inbox (an S3-backed
    // staging prefix) and Check-out (the caller's checked-out documents + their working-copy stash). Their WebDAV
    // paths are /webdav/Personal/Inbox and /webdav/Personal/Check-out — virtual (not Documents), shadowing any
    // real same-named child of Personal.
    private const string PersonalName = PersonalRepositoryProvisioner.PersonalRepositoryName;
    private const string InboxName = "Inbox";
    private const string CheckoutName = "Check-out";

    // True when the path addresses (or sits inside) one of the Personal-nested special folders:
    // [Personal, Inbox|Check-out, file?]. The special file, when present, is segments[2].
    private static bool IsSpecialPath(List<string> segments) =>
        segments.Count >= 2 && segments[0] == PersonalName && segments[1] is InboxName or CheckoutName;

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

        // Canonicalise a plain browser GET/HEAD of the legacy /webdav root to /SimplArchive (ADR 0509). A WebDAV
        // client never GETs the collection root — it PROPFINDs — so this 301 only ever hits a human/browser and
        // leaves real mounts (which use PROPFIND/PUT/… on /webdav, served directly as an alias) untouched.
        if (matchedBase == LegacyBasePath && method is "GET" or "HEAD"
            && path.TrimEnd('/').Equals(LegacyBasePath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect(BasePath, permanent: true);
            return;
        }

        var services = context.RequestServices;
        var db = services.GetRequiredService<SimplArchiveDbContext>();

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

        // The Personal repository is the home for the Inbox / Check-out folders and always appears at the WebDAV
        // root — ensure it exists (get-or-create, idempotent) before serving any request.
        await services.GetRequiredService<PersonalRepositoryProvisioner>().EnsureAsync(user.Id, user.TenantId, context.RequestAborted);

        var segments = path[matchedBase.Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();

        switch (method)
        {
            case "OPTIONS": HandleOptions(context); break;
            case "PROPFIND": await HandlePropFindAsync(context, services, db, user, segments); break;
            case "GET": await HandleGetAsync(context, services, db, user, segments, body: true); break;
            case "HEAD": await HandleGetAsync(context, services, db, user, segments, body: false); break;
            case "PUT": await HandlePutAsync(context, services, db, user, segments); break;
            case "MKCOL": await HandleMkColAsync(context, db, user, segments); break;
            case "DELETE": await HandleDeleteAsync(context, services, db, user, segments); break;
            case "MOVE": await HandleMoveAsync(context, services, db, user, segments); break;
            case "COPY": await HandleCopyAsync(context, services, db, user, segments); break;
            case "LOCK": HandleLock(context, user, segments); break;
            case "UNLOCK": HandleUnlock(context, user, segments); break;
            case "PROPPATCH": await HandlePropPatchAsync(context, db, user, segments); break;
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

    // Real exclusive write locks (ADR "WebDAV hardening"): LOCK acquires (or refreshes the caller's own) lock on
    // the path; a lock held by a *different* owner returns 423 Locked. The token is returned for the client to
    // present (via If / Lock-Token) on subsequent mutations.
    private const int DefaultLockSeconds = 3600;

    private void HandleLock(HttpContext context, User user, List<string> segments)
    {
        var pathKey = PathKey(segments);
        var timeout = TimeSpan.FromSeconds(ParseTimeoutSeconds(context));
        var lockInfo = _lockStore.TryLock(user.TenantId, pathKey, user.Id, timeout, DateTimeOffset.UtcNow);
        if (lockInfo is null)
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var seconds = (int)Math.Max(1, (lockInfo.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        var owner = Xml(user.Email);
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><D:prop xmlns:D="DAV:"><D:lockdiscovery><D:activelock><D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope><D:depth>0</D:depth><D:owner>{owner}</D:owner><D:timeout>Second-{seconds}</D:timeout><D:locktoken><D:href>{lockInfo.Token}</D:href></D:locktoken></D:activelock></D:lockdiscovery></D:prop>""";
        context.Response.Headers["Lock-Token"] = $"<{lockInfo.Token}>";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";
        context.Response.WriteAsync(xml).GetAwaiter().GetResult();
    }

    private void HandleUnlock(HttpContext context, User user, List<string> segments)
    {
        var token = context.Request.Headers["Lock-Token"].ToString().Trim().Trim('<', '>');
        context.Response.StatusCode = _lockStore.Unlock(user.TenantId, PathKey(segments), token)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status409Conflict;
    }

    // A mutating op is refused 423 Locked when a *different* owner holds an unexpired lock and the request didn't
    // present its token (in the If or Lock-Token header).
    private bool IsLocked(HttpContext context, User user, List<string> segments)
    {
        return _lockStore.IsBlocked(user.TenantId, PathKey(segments), user.Id, PresentedLockTokens(context), DateTimeOffset.UtcNow);
    }

    private static string PathKey(List<string> segments) => string.Join("/", segments);

    private static int ParseTimeoutSeconds(HttpContext context)
    {
        // Timeout: "Second-600" / "Infinite" (capped). Default 1 hour.
        var header = context.Request.Headers["Timeout"].ToString();
        var match = LockTimeoutRegex().Match(header);
        return match.Success && int.TryParse(match.Groups[1].Value, out var s) ? Math.Clamp(s, 1, 86400) : DefaultLockSeconds;
    }

    // Extracts the opaquelocktoken URIs a request presents in its If / Lock-Token headers (a pragmatic subset of
    // the full RFC 4918 If grammar — enough for common clients that echo the token they were issued).
    private static IReadOnlyCollection<string> PresentedLockTokens(HttpContext context)
    {
        var raw = $"{context.Request.Headers["If"]} {context.Request.Headers["Lock-Token"]}";
        return LockTokenRegex().Matches(raw).Select(m => m.Value).ToHashSet();
    }

    [GeneratedRegex(@"opaquelocktoken:[0-9a-fA-F-]+")]
    private static partial Regex LockTokenRegex();

    [GeneratedRegex(@"Second-(\d+)")]
    private static partial Regex LockTimeoutRegex();

    private static Task HandlePropPatchAsync(HttpContext context, SimplArchiveDbContext db, User user, List<string> segments) =>
        // We store no dead properties; accept the request as a no-op success so clients (esp. Finder setting
        // timestamps) don't fail the copy.
        WriteMultiStatusAsync(context, [new PropStatXml(HrefFor(segments), "HTTP/1.1 200 OK", "")]);

    // ---- PROPFIND ------------------------------------------------------------------------------------------
    private async Task HandlePropFindAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        var depth = context.Request.Headers["Depth"].ToString();
        depth = string.IsNullOrEmpty(depth) ? "1" : depth; // some clients omit Depth; default 1

        // The special Personal/Inbox and Personal/Check-out folders are backed by object storage / the check-out
        // entity, not the Document tree (ADR "WebDAV Inbox + Check-out folders").
        if (IsSpecialPath(segments))
        {
            await HandleSpecialPropFindAsync(context, services, db, user, segments, depth);
            return;
        }

        var calc = services.GetRequiredService<IEffectiveRightsCalculator>();
        var node = await ResolveAsync(db, user, segments);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // ACL: hide a resource the caller can't see (ADR "WebDAV hardening") — 404 rather than leaking it.
        if (node.Document is { } targetDoc && !(await calc.GetEffectiveRightsAsync(user.Id, targetDoc.Id)).CanSee)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var responses = new List<PropStatXml> { PropFor(node, segments) };
        if (depth != "0" && node.IsCollection)
        {
            foreach (var child in await ChildrenAsync(db, user, node, calc))
            {
                responses.Add(PropFor(child, [.. segments, child.WebDavName]));
            }
        }

        await WriteMultiStatusAsync(context, responses);
    }

    // PROPFIND for the special Personal/Inbox and Personal/Check-out folders (segments = [Personal, folder, file?]).
    // Resolves a single file inside a special (Inbox / Check-out) folder for GET/HEAD/PROPFIND. Beyond the listed
    // files, this also resolves the hidden lock/owner sidecars (.~lock.name# / ~$name) directly from the store —
    // they're kept out of the folder LISTING (so they don't clutter the view) but MUST round-trip, or LibreOffice /
    // Office read back their own just-PUT lock file, get 404, and revert the document to read-only (ADR 0513).
    private static async Task<SpecialFile?> ResolveSpecialFileAsync(IObjectStorageClient storage, SimplArchiveDbContext db, User user, string folder, string name)
    {
        var files = await SpecialFolderFilesAsync(storage, db, user, folder);
        if (files.FirstOrDefault(f => f.Name == name) is { } listed)
        {
            return listed;
        }

        if (IsLockFile(name) && !name.Contains('/'))
        {
            var key = folder == InboxName ? InboxPrefix(user) + name : CheckoutScratchPrefix(user) + name;
            if (await storage.ExistsAsync(key))
            {
                return new SpecialFile(name, await storage.GetObjectSizeAsync(key), DateTimeOffset.UtcNow, Guid.Empty, key);
            }
        }

        return null;
    }

    private async Task HandleSpecialPropFindAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, string depth)
    {
        var folder = segments[1];
        var storage = services.GetRequiredService<IObjectStorageClient>();

        if (segments.Count == 2)
        {
            // The Inbox / Check-out collection itself, plus (Depth 1) its files (lock/owner sidecars stay hidden).
            var files = await SpecialFolderFilesAsync(storage, db, user, folder);
            var responses = new List<PropStatXml> { CollectionProp([segments[0], folder], folder) };
            if (depth != "0")
            {
                responses.AddRange(files.Select(f => FileProp([segments[0], folder, f.Name], f.Size, f.Modified, ContentTypes.ForExtension(Path.GetExtension(f.Name)))));
            }

            await WriteMultiStatusAsync(context, responses);
            return;
        }

        // A single file inside the folder (flat — no deeper nesting), including a hidden lock/owner sidecar.
        if (segments.Count == 3 && await ResolveSpecialFileAsync(storage, db, user, folder, segments[2]) is { } file)
        {
            await WriteMultiStatusAsync(context, [FileProp(segments, file.Size, file.Modified, ContentTypes.ForExtension(Path.GetExtension(file.Name)))]);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static PropStatXml PropFor(WebDavNode node, List<string> segments)
    {
        var href = HrefFor(segments) + (node.IsCollection ? "/" : "");
        var props = new StringBuilder();
        props.Append($"<D:displayname>{Xml(node.WebDavName)}</D:displayname>");
        props.Append(node.IsCollection
            ? "<D:resourcetype><D:collection/></D:resourcetype>"
            : "<D:resourcetype/>");
        if (!node.IsCollection)
        {
            props.Append($"<D:getcontentlength>{node.Length}</D:getcontentlength>");
            props.Append($"<D:getcontenttype>{Xml(node.ContentType)}</D:getcontenttype>");
        }

        var modified = node.Modified.ToString("R", CultureInfo.InvariantCulture);
        props.Append($"<D:getlastmodified>{modified}</D:getlastmodified>");
        props.Append($"<D:creationdate>{node.Created.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</D:creationdate>");
        props.Append(SupportedLockXml); // advertise write-lock capability so editors open repository files read/write
        return new PropStatXml(href, "HTTP/1.1 200 OK", props.ToString());
    }

    // ---- GET / HEAD ----------------------------------------------------------------------------------------
    private async Task HandleGetAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, bool body)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();

        if (IsSpecialPath(segments))
        {
            if (segments.Count != 3 || await ResolveSpecialFileAsync(storage, db, user, segments[1], segments[2]) is not { } file)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await StreamAsync(context, storage, file.Key, ContentTypes.ForExtension(Path.GetExtension(file.Name)), file.Size, file.Modified, body);
            return;
        }

        var node = await ResolveAsync(db, user, segments);
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

        await StreamAsync(context, storage, node.ObjectKey!, node.ContentType, node.Length, node.Modified, body);
    }

    // Streams an object as a 200 (full) or a 206 Partial Content response, honoring a single Range header
    // (ADR "WebDAV hardening"); an unsatisfiable range → 416. Advertises Accept-Ranges: bytes either way.
    private static async Task StreamAsync(HttpContext context, IObjectStorageClient storage, string key, string contentType, long size, DateTimeOffset modified, bool body)
    {
        context.Response.ContentType = contentType;
        context.Response.Headers["Last-Modified"] = modified.ToString("R", CultureInfo.InvariantCulture);
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
        if (IsSpecialPath(segments))
        {
            await HandleSpecialPutAsync(context, services, db, user, segments);
            return;
        }

        if (segments.Count < 2)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't PUT at the repository-list root
            return;
        }

        if (IsLocked(context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var fileName = segments[^1];

        // A browser in-progress download (.crdownload/.part/.partial/.dltemp) is STAGED in the per-user temp
        // area (not dropped, not materialized as a document) and committed to a real document on the completing
        // MOVE (ADR "WebDAV .crdownload staging"). Checked before the clutter filter, which also matches these.
        if (IsDownloadTemp(fileName))
        {
            await StageDownloadTempAsync(context, services, db, user, segments);
            return;
        }

        // Don't file OS clutter (._*, .DS_Store, Thumbs.db, …) or other transient/editor temp files (~$*, .tmp,
        // .swp, …) as documents in the permanent archive — accept + silently discard (ADR "WebDAV clutter filter").
        if (IsOsClutter(fileName) || IsTransientClutter(fileName))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        var parent = await ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // parent missing / not a collection
            return;
        }

        var rights = await RightsAsync(services, user, parentDoc.Id);
        var existing = await ResolveAsync(db, user, segments);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // A LOCK/create dance from Finder sends a 0-byte PUT first; buffer the body to a temp object either way.
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var finalizer = services.GetRequiredService<DocumentFinalizer>();
        var now = DateTimeOffset.UtcNow;
        var objectKey = ObjectKeyBuilder.Build(user.TenantId, now, extension);
        // Buffer the body so object storage has a known content length (the request stream may be
        // chunked / non-seekable).
        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;

        // A zero-byte PUT to a NEW name is the OS's placeholder (Finder's LOCK/create dance, or a browser's
        // real-name placeholder while it streams the bytes into a sibling .crdownload) — don't materialize an
        // empty document; the real content arrives via a later PUT or the .crdownload → MOVE commit (ADR "WebDAV
        // .crdownload staging"). Accept + discard so the OS copy doesn't error.
        if (buffered.Length == 0 && existing is null)
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
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
            document = new Document { Id = Guid.NewGuid(), TenantId = user.TenantId, ParentId = parentDoc.Id, Name = stem, CreatedByUserId = user.Id, CreatedAt = now };
            db.Documents.Add(document);
            try { await db.SaveChangesAsync(context.RequestAborted); }
            catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; } // sibling-name clash
        }

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
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

    // Stage a browser in-progress download (.crdownload etc.) as an opaque object in the per-user temp area —
    // no Document is created; it's committed on the completing MOVE (ADR "WebDAV .crdownload staging").
    private async Task StageDownloadTempAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count < 2)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't PUT at the repository-list root
            return;
        }

        var parent = await ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // parent missing / not a collection
            return;
        }

        if (!(await RightsAsync(services, user, parentDoc.Id)).CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;

        if (!await services.GetRequiredService<IStorageQuotaService>().CanStoreAsync(user.TenantId, buffered.Length, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            return;
        }

        await services.GetRequiredService<IObjectStorageClient>().PutObjectAsync(
            TempKeyFor(user, segments), buffered, context.Request.ContentType ?? "application/octet-stream", context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    // Commit a staged download-temp on the MOVE that renames it to the final name: materialize the real Document
    // from the staged bytes + finalize, then drop the temp copy (ADR "WebDAV .crdownload staging"). Returns false
    // (nothing committed) when there's no staged blob — the caller then does a normal move.
    private async Task<bool> TryCommitDownloadTempAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var tempKey = TempKeyFor(user, segments);
        if (!await storage.ExistsAsync(tempKey, context.RequestAborted))
        {
            return false;
        }

        var destSegments = ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || IsSpecialPath(destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        var destParent = await ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return true;
        }

        if (!(await RightsAsync(services, user, destParentDoc.Id)).CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        var destName = destSegments[^1];
        var now = DateTimeOffset.UtcNow;
        var objectKey = ObjectKeyBuilder.Build(user.TenantId, now, Path.GetExtension(destName));

        // Server-side copy the staged blob to a real version key, then create the Document + finalize.
        await storage.CopyObjectAsync(tempKey, objectKey, context.RequestAborted);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            ParentId = destParentDoc.Id,
            Name = Path.GetFileNameWithoutExtension(destName),
            CreatedByUserId = user.Id,
            CreatedAt = now,
        };
        db.Documents.Add(document);
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (InvalidOperationException)
        {
            await storage.DeleteObjectAsync(objectKey, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status409Conflict; // sibling-name clash
            return true;
        }

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
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
        await services.GetRequiredService<DocumentFinalizer>().FinalizeAsync(version, context.RequestAborted);

        await storage.DeleteObjectAsync(tempKey, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        return true;
    }

    // ---- MKCOL (create folder) ----------------------------------------------------------------------------
    private async Task HandleMkColAsync(HttpContext context, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count < 2 || IsSpecialPath(segments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't create folders at the root, on the virtual Inbox/Check-out folders, or inside them
            return;
        }

        // Silently accept OS-junk directories (.Trashes, .TemporaryItems, .fseventsd, .Spotlight-V100 …) without
        // creating a folder document (ADR "WebDAV clutter filter").
        if (IsOsClutter(segments[^1]))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        if (IsLocked(context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var parent = await ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (await ResolveAsync(db, user, segments) is not null)
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
        if (IsSpecialPath(segments))
        {
            if (segments.Count != 3)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var storage = services.GetRequiredService<IObjectStorageClient>();
            var name = segments[2];

            if (segments[1] == InboxName)
            {
                if ((await InboxFilesAsync(storage, user)).All(f => f.Name != name) && !IsLockFile(name))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await storage.DeleteObjectAsync(InboxPrefix(user) + name, context.RequestAborted);
                try { await storage.DeleteObjectAsync(InboxPrefix(user) + name + ".mask.json", context.RequestAborted); } catch (Exception) { /* sidecar may not exist */ }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // Check-out (ADR 0508): deleting a checked-out doc's name is the editor's pre-rename delete — a no-op
            // (the check-out is released only via the client); deleting a scratch temp/lock file removes it.
            if ((await CheckoutFilesAsync(storage, db, user)).Any(f => f.Name == name))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent; // no-op: keep the check-out
                return;
            }

            var scratchKey = CheckoutScratchPrefix(user) + name;
            if (await storage.ExistsAsync(scratchKey, context.RequestAborted))
            {
                await storage.DeleteObjectAsync(scratchKey, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = IsLockFile(name) ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
            return;
        }

        // A browser cancelling/finishing a download deletes its in-progress temp file; drop any staged blob and
        // succeed (there is no Document to remove). ADR "WebDAV .crdownload staging".
        if (IsDownloadTemp(segments[^1]))
        {
            var storage = services.GetRequiredService<IObjectStorageClient>();
            var tempKey = TempKeyFor(user, segments);
            if (await storage.ExistsAsync(tempKey, context.RequestAborted))
            {
                await storage.DeleteObjectAsync(tempKey, context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (IsLocked(context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var node = await ResolveAsync(db, user, segments);
        if (node?.Document is not { } document)
        {
            context.Response.StatusCode = node is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden; // can't delete a virtual root/repository listing
            return;
        }

        if (!(await RightsAsync(services, user, document.Id)).CanDelete)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Soft-delete the subtree (the same cascade as the API's DELETE).
        foreach (var d in await CollectSubtreeAsync(db, document.Id))
        {
            d.DeletedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // ---- MOVE (reparent + rename) -------------------------------------------------------------------------
    private async Task HandleMoveAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (IsSpecialPath(segments))
        {
            await HandleSpecialRenameAsync(context, services, db, user, segments, keepSource: false); // atomic-save rename within Inbox/Check-out (ADR 0508)
            return;
        }

        if (IsLocked(context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        // Commit-on-rename: a browser finishing a download renames its in-progress temp (X.crdownload) to the
        // final name — if the source is a staged download-temp, materialize the real document from the staged
        // bytes (ADR "WebDAV .crdownload staging"). Falls through to a normal move when nothing is staged.
        if (IsDownloadTemp(segments[^1]) && await TryCommitDownloadTempAsync(context, services, db, user, segments))
        {
            return;
        }

        var node = await ResolveAsync(db, user, segments);
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
        if (destSegments is null || destSegments.Count < 2 || IsSpecialPath(destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // no/blank destination, the root, or a special folder
            return;
        }

        if (IsLocked(context, user, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var newName = Path.GetFileNameWithoutExtension(destSegments[^1]);
        var destParent = await ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
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

    // MOVE / COPY within a special folder — the rename/duplicate steps of an editor's atomic save (ADR 0508).
    // keepSource=false for MOVE (rename), true for COPY (duplicate). The destination must be the SAME special
    // folder (cross-folder moves aren't supported). In Check-out:
    //  • scratch temp → a checked-out document = the commit (write the bytes to that document's stash);
    //  • scratch temp → another name = duplicate/rename the temp;
    //  • a checked-out document → a scratch name = copy the document's CURRENT working bytes out to a scratch
    //    backup (macOS's replaceItemAtURL renames the original away before dropping the new file in) — the
    //    document itself stays checked out and in place.
    // In the Inbox it renames/duplicates the staged object. So every combination office/PDF editors emit —
    // temp+rename, temp+copy, delete-then-rename, or the rename-original-to-backup dance — resolves correctly.
    private async Task HandleSpecialRenameAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, bool keepSource)
    {
        var destSegments = ParseDestination(context);
        if (segments.Count != 3 || destSegments is not { Count: 3 } || !IsSpecialPath(destSegments)
            || destSegments[0] != segments[0] || destSegments[1] != segments[1])
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // only same-special-folder renames are supported
            return;
        }

        var storage = services.GetRequiredService<IObjectStorageClient>();
        var (srcName, destName) = (segments[2], destSegments[2]);

        if (segments[1] == InboxName)
        {
            var srcKey = InboxPrefix(user) + srcName;
            if (!await storage.ExistsAsync(srcKey, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var destKey = InboxPrefix(user) + destName;
            var inboxDestExisted = await storage.ExistsAsync(destKey, context.RequestAborted);
            await storage.CopyObjectAsync(srcKey, destKey, context.RequestAborted);
            if (!keepSource) await storage.DeleteObjectAsync(srcKey, context.RequestAborted);
            context.Response.StatusCode = inboxDestExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
            return;
        }

        // Check-out.
        var docs = await CheckoutFilesAsync(storage, db, user);
        var scratchSrcKey = CheckoutScratchPrefix(user) + srcName;
        var srcIsScratch = await storage.ExistsAsync(scratchSrcKey, context.RequestAborted);

        // Source is a checked-out document → copy its current working bytes out to a scratch backup; the document
        // stays put (a document is never renamed/removed over WebDAV — the check-out is a client action).
        if (!srcIsScratch && docs.FirstOrDefault(f => f.Name == srcName) is { } srcDoc)
        {
            if (docs.Any(f => f.Name == destName)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; } // doc → doc unsupported
            var backupKey = CheckoutScratchPrefix(user) + destName;
            var backupExisted = await storage.ExistsAsync(backupKey, context.RequestAborted);
            await storage.CopyObjectAsync(srcDoc.Key, backupKey, context.RequestAborted);
            context.Response.StatusCode = backupExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
            return;
        }

        if (!srcIsScratch)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Scratch → a checked-out document = the commit: write the scratch bytes to that document's stash.
        if (docs.FirstOrDefault(f => f.Name == destName) is { } targetDoc)
        {
            using var buffered = new MemoryStream();
            await using (var scratch = await storage.GetObjectAsync(scratchSrcKey, context.RequestAborted))
            {
                await scratch.CopyToAsync(buffered, context.RequestAborted);
            }

            buffered.Position = 0;
            await storage.PutObjectAsync(
                CheckoutStashKey.Build(user.TenantId, user.Id, targetDoc.DocumentId),
                buffered, ContentTypes.ForExtension(Path.GetExtension(destName)), context.RequestAborted);
            if (!keepSource) await storage.DeleteObjectAsync(scratchSrcKey, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Scratch → scratch = duplicate/rename the temp.
        var scratchDestKey = CheckoutScratchPrefix(user) + destName;
        var scratchDestExisted = await storage.ExistsAsync(scratchDestKey, context.RequestAborted);
        await storage.CopyObjectAsync(scratchSrcKey, scratchDestKey, context.RequestAborted);
        if (!keepSource) await storage.DeleteObjectAsync(scratchSrcKey, context.RequestAborted);
        context.Response.StatusCode = scratchDestExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    // Parses the Destination header into WebDAV path segments (null when absent/unparseable).
    private static List<string>? ParseDestination(HttpContext context)
    {
        var destination = context.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destination))
        {
            return null;
        }

        var uri = new Uri(destination, UriKind.RelativeOrAbsolute);
        var absolute = uri.IsAbsoluteUri ? uri.AbsolutePath : destination;
        // The Destination may use either the canonical /SimplArchive or the legacy /webdav prefix.
        var baseIndex = absolute.IndexOf(BasePath, StringComparison.OrdinalIgnoreCase);
        var matchedLength = BasePath.Length;
        if (baseIndex < 0)
        {
            baseIndex = absolute.IndexOf(LegacyBasePath, StringComparison.OrdinalIgnoreCase);
            matchedLength = LegacyBasePath.Length;
        }

        var tail = baseIndex >= 0 ? absolute[(baseIndex + matchedLength)..] : absolute;
        return tail.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
    }

    // ---- COPY (duplicate a file or a folder subtree) ------------------------------------------------------
    private async Task HandleCopyAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (IsSpecialPath(segments))
        {
            await HandleSpecialRenameAsync(context, services, db, user, segments, keepSource: true); // atomic-save copy within Inbox/Check-out (ADR 0508)
            return;
        }

        var source = await ResolveAsync(db, user, segments);
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
        if (destSegments is null || destSegments.Count < 2 || IsSpecialPath(destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (IsLocked(context, user, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        if (!(await RightsAsync(services, user, sourceDoc.Id)).CanReadContent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var destParent = await ResolveAsync(db, user, destSegments[..^1]);
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
        var existing = await ResolveAsync(db, user, destSegments);
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

        var newKey = ObjectKeyBuilder.Build(user.TenantId, now, Path.GetExtension(version.ObjectKey));
        await storage.CopyObjectAsync(version.ObjectKey, newKey, ct);

        var doc = new Document { Id = Guid.NewGuid(), TenantId = user.TenantId, ParentId = destParentId, Name = newName, CreatedByUserId = user.Id, CreatedAt = now };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);

        var newVersion = new DocumentVersion
        {
            Id = Guid.NewGuid(),
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

    // ---- Resolution: WebDAV path segments → a node --------------------------------------------------------
    private static async Task<WebDavNode?> ResolveAsync(SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count == 0)
        {
            return WebDavNode.Root();
        }

        // First segment = a repository name (the user's Personal space, or a shared repository they can see).
        var roots = await RootsAsync(db, user);
        if (roots.FirstOrDefault(r => r.Name == segments[0]) is not { } repo)
        {
            return null;
        }

        var current = repo;
        for (var i = 1; i < segments.Count; i++)
        {
            var child = await ChildByWebDavNameAsync(db, current.Id, segments[i]);
            if (child is null)
            {
                return null;
            }

            current = child;
        }

        return await NodeForAsync(db, current);
    }

    private static async Task<Document?> ChildByWebDavNameAsync(SimplArchiveDbContext db, Guid parentId, string webDavName)
    {
        // A folder's WebDAV name is its Name; a file's is Name + extension. Name (the stem) is unique per parent,
        // so match the folder name first, else the file stem.
        var byName = await db.Documents.SingleOrDefaultAsync(d => d.ParentId == parentId && d.Name == webDavName);
        if (byName is not null)
        {
            return byName;
        }

        var stem = Path.GetFileNameWithoutExtension(webDavName);
        return await db.Documents.SingleOrDefaultAsync(d => d.ParentId == parentId && d.Name == stem);
    }

    // ---- Special Inbox / Check-out areas -----------------------------------------------------------------
    private sealed record SpecialFile(string Name, long Size, DateTimeOffset Modified, Guid DocumentId, string Key);

    private static string InboxPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/inbox/";

    // Cached preview/text-layout artifacts + staged mask sidecars never appear as inbox items (ADR "Avoid inbox
    // preview litter").
    private static bool IsInboxLitter(string name) =>
        name.Contains(".preview.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mask.json", StringComparison.OrdinalIgnoreCase);

    // OS metadata clutter written by Finder / Explorer when browsing a mounted WebDAV volume — never wanted
    // ANYWHERE (repo, Inbox, or Check-out): macOS AppleDouble (._*), .DS_Store, the Spotlight/Trash/fsevents
    // dot-dirs, and Windows Thumbs.db / desktop.ini. Silently accepted-and-discarded (a copy in Finder/Explorer
    // succeeds; the junk is never stored). ADR "WebDAV clutter filter".
    private static readonly HashSet<string> OsClutterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", ".localized", ".apdisk", ".VolumeIcon.icns", "Thumbs.db", "ehthumbs.db", "desktop.ini",
    };

    private static bool IsOsClutter(string name) =>
        name.StartsWith("._", StringComparison.Ordinal)
        || OsClutterNames.Contains(name)
        || name.StartsWith(".Spotlight-V100", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".Trashes", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".fseventsd", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".TemporaryItems", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".DocumentRevisions-V100", StringComparison.OrdinalIgnoreCase);

    // Transient / partial-download / editor-temp files. Legitimate in the Inbox / Check-out staging areas (e.g. an
    // in-progress download), but should NOT land in the permanent repository. ADR "WebDAV clutter filter".
    private static readonly HashSet<string> TransientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".tmp", ".temp", ".swp", ".swx",
    };

    private static bool IsTransientClutter(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) // Office lock/temp files
        || TransientExtensions.Contains(Path.GetExtension(name));

    // Browser in-progress-download temp files (ADR "WebDAV .crdownload staging"). When a browser downloads a file
    // INTO a mounted WebDAV folder it writes the bytes to one of these (Chromium .crdownload, Firefox .part,
    // IE/legacy-Edge .partial, legacy Opera .dltemp) and renames it to the final name on completion. Rather than
    // dropping these as clutter (losing the bytes) or letting the OS's zero-byte placeholder create an empty
    // document, we STAGE them in a per-user temp area and materialize the real document on the completing MOVE.
    // (Safari's .download is a directory bundle, out of scope; other transient/editor temps stay dropped clutter.)
    private static readonly HashSet<string> DownloadTempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".dltemp",
    };

    private static bool IsDownloadTemp(string name) => DownloadTempExtensions.Contains(Path.GetExtension(name));

    // Per-user temp staging area for in-progress downloads — the same tier as inbox/ and checkout/ (ADR 0368).
    private static string TempPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/temp/";

    // A staged download-temp's object key is derived from its WebDAV path so the PUT (stage) and the later MOVE
    // (commit) resolve the same object across requests. Hashed to keep the key opaque + free of path characters.
    private static string TempKeyFor(User user, List<string> segments) =>
        TempPrefix(user) + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('/', segments))))
            .ToLowerInvariant();

    private static async Task<List<SpecialFile>> InboxFilesAsync(IObjectStorageClient storage, User user)
    {
        var prefix = InboxPrefix(user);
        var result = new List<SpecialFile>();
        foreach (var obj in await storage.ListObjectsAsync(prefix))
        {
            var name = obj.Key[prefix.Length..];
            if (name.Length == 0 || name.Contains('/') || IsInboxLitter(name) || IsOsClutter(name) || IsLockFile(name))
            {
                continue; // the prefix placeholder, a nested key, a litter artifact, OS clutter, or a lock file
            }

            result.Add(new SpecialFile(name, obj.Size, obj.LastModified, Guid.Empty, obj.Key));
        }

        return result;
    }

    // The caller's checked-out documents, shown by name; the working copy is the cloud stash if present, else
    // the current confirmed version (ADR "Document check-out / check-in" stash).
    // Office/LibreOffice owner + lock files (~$name / .~lock.name#) — hidden from the special-folder listings so
    // they don't clutter the view while an edit is in flight (ADR 0508). They still PUT/DELETE like any file.
    private static bool IsLockFile(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal)
        || (name.StartsWith(".~lock.", StringComparison.Ordinal) && name.EndsWith("#", StringComparison.Ordinal));

    // Per-user scratch area for the Check-out folder's in-flight atomic-save temp files (ADR 0508) — the same
    // tier as inbox/ and checkout/ (ADR 0368). A temp is committed to the doc's stash on the rename MOVE.
    private static string CheckoutScratchPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/checkout-scratch/";

    private static async Task<List<SpecialFile>> CheckoutScratchFilesAsync(IObjectStorageClient storage, User user)
    {
        var prefix = CheckoutScratchPrefix(user);
        var result = new List<SpecialFile>();
        foreach (var obj in await storage.ListObjectsAsync(prefix))
        {
            var name = obj.Key[prefix.Length..];
            if (name.Length == 0 || name.Contains('/') || IsLockFile(name))
            {
                continue; // the prefix placeholder, a nested key, or a hidden lock/owner file
            }

            result.Add(new SpecialFile(name, obj.Size, obj.LastModified, Guid.Empty, obj.Key));
        }

        return result;
    }

    // The files a special folder exposes over WebDAV: the Inbox's staged objects, or the Check-out's checked-out
    // documents PLUS any in-flight atomic-save scratch temps (ADR 0508).
    private static async Task<List<SpecialFile>> SpecialFolderFilesAsync(IObjectStorageClient storage, SimplArchiveDbContext db, User user, string folder)
    {
        if (folder == InboxName)
        {
            return await InboxFilesAsync(storage, user);
        }

        var files = await CheckoutFilesAsync(storage, db, user);
        files.AddRange(await CheckoutScratchFilesAsync(storage, user));
        return files;
    }

    private static async Task<List<SpecialFile>> CheckoutFilesAsync(IObjectStorageClient storage, SimplArchiveDbContext db, User user)
    {
        var checkedOut = await db.Documents.Where(d => d.CheckedOutByUserId == user.Id).ToListAsync();
        var result = new List<SpecialFile>();
        foreach (var doc in checkedOut)
        {
            // Current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
            var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, doc.Id, doc.CurrentVersionId);
            if (version is null)
            {
                continue;
            }

            var stashKey = CheckoutStashKey.Build(user.TenantId, user.Id, doc.Id);
            var hasStash = await storage.ExistsAsync(stashKey);
            var key = hasStash ? stashKey : version.ObjectKey;
            var size = hasStash ? await storage.GetObjectSizeAsync(stashKey) : version.SizeBytes ?? 0;
            var name = doc.Name + Path.GetExtension(version.ObjectKey);
            result.Add(new SpecialFile(name, size, doc.CheckedOutAt ?? doc.CreatedAt, doc.Id, key));
        }

        return result;
    }

    private async Task HandleSpecialPutAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count != 3)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // the special folders are flat
            return;
        }

        var name = segments[2];

        // OS metadata junk is discarded even in the staging areas; transient files (.crdownload etc.) are allowed
        // here (unlike the repository) — ADR "WebDAV clutter filter".
        if (IsOsClutter(name))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        var storage = services.GetRequiredService<IObjectStorageClient>();
        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;
        var contentType = context.Request.ContentType ?? "application/octet-stream";

        if (segments[1] == InboxName)
        {
            // Stage a raw object in the inbox prefix — no Document is created (the staging semantics; it's filed
            // later from the Inbox tab). ADR "S3-backed inbox" / "WebDAV Inbox + Check-out folders".
            if (IsInboxLitter(name))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var key = InboxPrefix(user) + name;
            var existed = await storage.ExistsAsync(key, context.RequestAborted);
            await storage.PutObjectAsync(key, buffered, contentType, context.RequestAborted);
            context.Response.StatusCode = existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
            return;
        }

        // Check-out: a PUT onto a checked-out document's name saves the working copy to that doc's stash (the
        // "Save to cloud" path). A PUT to any OTHER name is an atomic-save temp/lock/owner file — buffer it in the
        // per-user scratch area so the later rename MOVE can commit it (ADR 0508). Creating a check-out over WebDAV
        // is still not supported.
        var files = await CheckoutFilesAsync(storage, db, user);
        if (files.FirstOrDefault(f => f.Name == name) is { } file)
        {
            await storage.PutObjectAsync(CheckoutStashKey.Build(user.TenantId, user.Id, file.DocumentId), buffered, contentType, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var scratchKey = CheckoutScratchPrefix(user) + name;
        var scratchExisted = await storage.ExistsAsync(scratchKey, context.RequestAborted);
        await storage.PutObjectAsync(scratchKey, buffered, contentType, context.RequestAborted);
        context.Response.StatusCode = scratchExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    // Advertise write-lock capability on every resource (exclusive + shared write locks). The server already sends
    // DAV: 1, 2 on OPTIONS, but lock-checking editors (LibreOffice, MS Office) read the per-resource
    // <D:supportedlock> property in PROPFIND to decide a file is writable — without it they open read-only even
    // though a LOCK would succeed. (ADR 0508 WebDAV atomic-save; the LOCK/UNLOCK handlers back a real lock store.)
    private const string SupportedLockXml =
        "<D:supportedlock>" +
        "<D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>" +
        "<D:lockentry><D:lockscope><D:shared/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>" +
        "</D:supportedlock>";

    private static PropStatXml CollectionProp(List<string> segments, string displayName)
    {
        var props = $"<D:displayname>{Xml(displayName)}</D:displayname><D:resourcetype><D:collection/></D:resourcetype><D:getlastmodified>{DateTimeOffset.UnixEpoch.ToString("R", CultureInfo.InvariantCulture)}</D:getlastmodified>{SupportedLockXml}";
        return new PropStatXml(HrefFor(segments) + "/", "HTTP/1.1 200 OK", props);
    }

    private static PropStatXml FileProp(List<string> segments, long size, DateTimeOffset modified, string contentType)
    {
        var props = new StringBuilder();
        props.Append($"<D:displayname>{Xml(segments[^1])}</D:displayname><D:resourcetype/>");
        props.Append($"<D:getcontentlength>{size}</D:getcontentlength>");
        props.Append($"<D:getcontenttype>{Xml(contentType)}</D:getcontenttype>");
        props.Append($"<D:getlastmodified>{modified.ToString("R", CultureInfo.InvariantCulture)}</D:getlastmodified>");
        props.Append(SupportedLockXml);
        return new PropStatXml(HrefFor(segments), "HTTP/1.1 200 OK", props.ToString());
    }

    private static async Task<List<Document>> RootsAsync(SimplArchiveDbContext db, User user)
    {
        // The user's Personal repository + shared repositories (root documents). ACL is enforced per operation;
        // the listing here is intentionally simple (the tenant filter already scopes it). The Personal repository
        // is ordered first so /webdav/Personal resolves to it even if a shared repository shares the name.
        var roots = await db.Documents
            .Where(d => d.ParentId == null && (d.PersonalOfUserId == null || d.PersonalOfUserId == user.Id))
            .ToListAsync();
        return roots.OrderByDescending(d => d.PersonalOfUserId == user.Id).ToList();
    }

    private static async Task<List<WebDavNode>> ChildrenAsync(SimplArchiveDbContext db, User user, WebDavNode node, IEffectiveRightsCalculator calc)
    {
        if (node.IsRoot)
        {
            // The root lists the repositories the caller can see (ADR "WebDAV hardening"): the Personal repo is
            // always the user's own; shared roots are CanSee-filtered. Inbox / Check-out live under Personal now.
            var visible = new List<WebDavNode>();
            foreach (var root in await RootsAsync(db, user))
            {
                if (root.PersonalOfUserId == user.Id || (await calc.GetEffectiveRightsAsync(user.Id, root.Id)).CanSee)
                {
                    visible.Add(WebDavNode.Collection(root));
                }
            }

            return visible;
        }

        var children = await db.Documents.Where(d => d.ParentId == node.Document!.Id).ToListAsync();
        var result = new List<WebDavNode>();

        // The Personal repository holds the two virtual special folders, which shadow any real same-named child.
        if (node.Document!.PersonalOfUserId == user.Id)
        {
            result.Add(WebDavNode.Special(InboxName));   // the per-user Inbox staging folder
            result.Add(WebDavNode.Special(CheckoutName)); // the caller's checked-out documents
            children = children.Where(c => c.Name is not (InboxName or CheckoutName)).ToList();
        }

        // ACL-filter each child by CanSee (ADR "WebDAV hardening").
        foreach (var child in children)
        {
            if ((await calc.GetEffectiveRightsAsync(user.Id, child.Id)).CanSee)
            {
                result.Add(await NodeForAsync(db, child));
            }
        }

        return result;
    }

    private static async Task<WebDavNode> NodeForAsync(SimplArchiveDbContext db, Document document)
    {
        // The document's current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
        var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, document.Id, document.CurrentVersionId);

        if (version is null)
        {
            return WebDavNode.Collection(document); // no version → a folder
        }

        var extension = Path.GetExtension(version.ObjectKey);
        return new WebDavNode
        {
            Document = document,
            IsCollection = false,
            WebDavName = document.Name + extension,
            ObjectKey = version.ObjectKey,
            Length = version.SizeBytes ?? 0,
            ContentType = ContentTypes.ForExtension(extension),
            Created = document.CreatedAt,
            Modified = version.CreatedAt,
        };
    }

    private static async Task<List<Document>> CollectSubtreeAsync(SimplArchiveDbContext db, Guid rootId)
    {
        var subtree = new List<Document>();
        var level = new List<Guid> { rootId };
        subtree.Add(await db.Documents.SingleAsync(d => d.Id == rootId));
        while (level.Count > 0)
        {
            var children = await db.Documents.Where(d => d.ParentId != null && level.Contains(d.ParentId!.Value)).ToListAsync();
            if (children.Count == 0)
            {
                break;
            }

            subtree.AddRange(children);
            level = children.Select(c => c.Id).ToList();
        }

        return subtree;
    }

    private static async Task<EffectiveRights> RightsAsync(IServiceProvider services, User user, Guid documentId) =>
        await services.GetRequiredService<IEffectiveRightsCalculator>().GetEffectiveRightsAsync(user.Id, documentId);

    private static string HrefFor(List<string> segments) =>
        BasePath + string.Concat(segments.Select(s => "/" + Uri.EscapeDataString(s)));

    private static async Task WriteMultiStatusAsync(HttpContext context, List<PropStatXml> responses)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
        foreach (var r in responses)
        {
            sb.Append("<D:response>");
            sb.Append($"<D:href>{Xml(r.Href)}</D:href>");
            sb.Append("<D:propstat>");
            sb.Append($"<D:prop>{r.Props}</D:prop>");
            sb.Append($"<D:status>{r.Status}</D:status>");
            sb.Append("</D:propstat></D:response>");
        }

        sb.Append("</D:multistatus>");
        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(sb.ToString(), context.RequestAborted);
    }

    private static string Xml(string value) => new XmlDocument().CreateTextNode(value).OuterXml;

    private sealed record PropStatXml(string Href, string Status, string Props);

    // A resolved WebDAV resource: the virtual repository-list root, a collection (folder/root Document), or a
    // file (Document with a current version).
    private sealed class WebDavNode
    {
        public Document? Document { get; init; }
        public bool IsRoot { get; init; }
        public bool IsCollection { get; init; }
        public string WebDavName { get; init; } = "";
        public string? ObjectKey { get; init; }
        public long Length { get; init; }
        public string ContentType { get; init; } = "application/octet-stream";
        public DateTimeOffset Created { get; init; }
        public DateTimeOffset Modified { get; init; }

        // The single mounted resource is named "SimplArchive" and its children mirror the Repositories tree-pane
        // exactly (ADR 0509): the Personal space, then the shared repositories the caller can see.
        public static WebDavNode Root() => new() { IsRoot = true, IsCollection = true, WebDavName = "SimplArchive", Created = DateTimeOffset.UnixEpoch, Modified = DateTimeOffset.UnixEpoch };

        public static WebDavNode Collection(Document document) => new()
        {
            Document = document,
            IsCollection = true,
            WebDavName = document.Name,
            Created = document.CreatedAt,
            Modified = document.CreatedAt,
        };

        // A special top-level folder (Inbox / Check-out) — a collection not backed by a Document.
        public static WebDavNode Special(string name) => new()
        {
            IsCollection = true,
            WebDavName = name,
            Created = DateTimeOffset.UnixEpoch,
            Modified = DateTimeOffset.UnixEpoch,
        };
    }
}
