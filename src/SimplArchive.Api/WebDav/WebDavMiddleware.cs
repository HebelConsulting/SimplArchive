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
    // displayname (ADR 0509). The original /webdav path stays accepted for already-saved mounts; hrefs are always
    // emitted under /SimplArchive (the canonical path).
    public const string BasePath = "/SimplArchive";
    private const string LegacyBasePath = "/webdav";

    // The gateway answers at /SimplArchive (canonical) and /webdav (legacy). Returns whichever prefix the request
    // used, or null when the path isn't the gateway's.
    /// <summary>
    /// Whether this path is the WebDAV gateway's — asked by the DAV wire trace, which must cover this surface
    /// too (#595). One matcher, so the trace cannot disagree with the gateway about what it serves.
    /// </summary>
    internal static bool IsGatewayPath(string path) => MatchedBase(path) is not null;

    private static string? MatchedBase(string path) =>
        path.Equals(BasePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(BasePath + "/", StringComparison.OrdinalIgnoreCase) ? BasePath
        : path.Equals(LegacyBasePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(LegacyBasePath + "/", StringComparison.OrdinalIgnoreCase) ? LegacyBasePath
        : null;

    // Two special folders nested under the caller's Personal repository (ADR "WebDAV Inbox + Check-out folders",
    // grouped under Personal by ADR "WebDAV Inbox/Check-out under Personal"): the per-user Intray (an S3-backed
    // staging prefix) and Check-out (the caller's checked-out documents + their working-copy stash). Their WebDAV
    // paths are /webdav/Personal/Intray and /webdav/Personal/Check-out — virtual (not Documents), shadowing any
    // real same-named child of Personal.
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
    internal static bool IsSpecialPath(HttpContext context, List<string> segments) =>
        segments.Count >= 2 && segments[0] == PersonalNameFor(context) && segments[1] is IntrayName or CheckoutName;

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

        // The Personal repository is the home for the Intray / Check-out folders and always appears at the WebDAV
        // root — ensure it exists (get-or-create, idempotent) before serving any request.
        var personalRoot = await services.GetRequiredService<PersonalRepositoryProvisioner>().EnsureAsync(user.Id, user.TenantId, context.RequestAborted);
        context.Items[PersonalNameKey] = personalRoot.Name;

        var segments = path[matchedBase.Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();

        switch (method)
        {
            case "OPTIONS": HandleOptions(context); break;
            case "PROPFIND": await HandlePropFindAsync(context, services, db, user, segments, matchedBase); break;
            case "GET": await HandleGetAsync(context, services, db, user, segments, body: true); break;
            case "HEAD": await HandleGetAsync(context, services, db, user, segments, body: false); break;
            case "PUT": await HandlePutAsync(context, services, db, user, segments); break;
            case "MKCOL": await HandleMkColAsync(context, db, user, segments); break;
            case "DELETE": await HandleDeleteAsync(context, services, db, user, segments); break;
            case "MOVE": await HandleMoveAsync(context, services, db, user, segments); break;
            case "COPY": await HandleCopyAsync(context, services, db, user, segments); break;
            case "LOCK": WebDavLockHandling.HandleLock(_lockStore, context, user, segments); break;
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

        var calc = services.GetRequiredService<IEffectiveRightsCalculator>();
        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
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

        var responses = new List<PropStatXml> { PropFor(node, segments, basePath) };
        if (depth != "0" && node.IsCollection)
        {
            foreach (var child in await WebDavPathResolver.ChildrenAsync(db, user, node, calc))
            {
                responses.Add(PropFor(child, [.. segments, child.WebDavName], basePath));
            }
        }

        await WebDavXml.WriteMultiStatusAsync(context, responses);
    }



    private static PropStatXml PropFor(WebDavNode node, List<string> segments, string basePath)
    {
        var href = WebDavPathResolver.HrefFor(basePath, segments) + (node.IsCollection ? "/" : "");
        var props = new StringBuilder();
        props.Append($"<D:displayname>{WebDavXml.Xml(node.WebDavName)}</D:displayname>");
        props.Append(node.IsCollection
            ? "<D:resourcetype><D:collection/></D:resourcetype>"
            : "<D:resourcetype/>");
        if (!node.IsCollection)
        {
            props.Append($"<D:getcontentlength>{node.Length}</D:getcontentlength>");
            props.Append($"<D:getcontenttype>{WebDavXml.Xml(node.ContentType)}</D:getcontenttype>");
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

        if (IsSpecialPath(context, segments))
        {
            if (segments.Count != 3 || await WebDavUserAreas.ResolveSpecialFileAsync(storage, db, user, segments[1], segments[2]) is not { } file)
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
        if (IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialPutAsync(context, services, db, user, segments);
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

        // A browser in-progress download (.crdownload/.part/.partial/.dltemp) is STAGED in the per-user temp
        // area (not dropped, not materialized as a document) and committed to a real document on the completing
        // MOVE (ADR "WebDAV .crdownload staging"). Checked before the clutter filter, which also matches these.
        if (WebDavClutter.IsDownloadTemp(fileName))
        {
            await WebDavSpecialHandlers.StageDownloadTempAsync(context, services, db, user, segments);
            return;
        }

        // Don't file OS clutter (._*, .DS_Store, Thumbs.db, …) as documents in the permanent archive — accept +
        // silently discard (ADR "WebDAV clutter filter").
        if (WebDavClutter.IsOsClutter(fileName))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
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
    private async Task HandleMkColAsync(HttpContext context, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count < 2 || IsSpecialPath(context, segments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't create folders at the root, on the virtual Intray/Check-out folders, or inside them
            return;
        }

        // Silently accept OS-junk directories (.Trashes, .TemporaryItems, .fseventsd, .Spotlight-V100 …) without
        // creating a folder document (ADR "WebDAV clutter filter").
        if (WebDavClutter.IsOsClutter(segments[^1]))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
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
            context.Response.StatusCode = node is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden; // can't delete a virtual root/repository listing
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

    internal static PropStatXml CollectionProp(string basePath, List<string> segments, string displayName)
    {
        var props = $"<D:displayname>{WebDavXml.Xml(displayName)}</D:displayname><D:resourcetype><D:collection/></D:resourcetype><D:getlastmodified>{DateTimeOffset.UnixEpoch.ToString("R", CultureInfo.InvariantCulture)}</D:getlastmodified>{SupportedLockXml}";
        return new PropStatXml(WebDavPathResolver.HrefFor(basePath, segments) + "/", "HTTP/1.1 200 OK", props);
    }

    internal static PropStatXml FileProp(string basePath, List<string> segments, long size, DateTimeOffset modified, string contentType)
    {
        var props = new StringBuilder();
        props.Append($"<D:displayname>{WebDavXml.Xml(segments[^1])}</D:displayname><D:resourcetype/>");
        props.Append($"<D:getcontentlength>{size}</D:getcontentlength>");
        props.Append($"<D:getcontenttype>{WebDavXml.Xml(contentType)}</D:getcontenttype>");
        props.Append($"<D:getlastmodified>{modified.ToString("R", CultureInfo.InvariantCulture)}</D:getlastmodified>");
        props.Append(SupportedLockXml);
        return new PropStatXml(WebDavPathResolver.HrefFor(basePath, segments), "HTTP/1.1 200 OK", props.ToString());
    }





    internal static async Task<EffectiveRights> RightsAsync(IServiceProvider services, User user, Guid documentId) =>
        await services.GetRequiredService<IEffectiveRightsCalculator>().GetEffectiveRightsAsync(user.Id, documentId);





}
