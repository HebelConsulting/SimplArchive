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
            case "PROPFIND": await WebDavReads.HandlePropFindAsync(context, services, db, user, segments, matchedBase); break;
            case "GET": await WebDavReads.HandleGetAsync(context, services, db, user, segments, body: true); break;
            case "HEAD": await WebDavReads.HandleGetAsync(context, services, db, user, segments, body: false); break;
            case "PUT": await WebDavWrites.HandlePutAsync(_lockStore, context, services, db, user, segments); break;
            case "MKCOL": await WebDavWrites.HandleMkColAsync(_lockStore, context, services, db, user, segments); break;
            case "DELETE": await WebDavWrites.HandleDeleteAsync(_lockStore, context, services, db, user, segments); break;
            case "MOVE": await WebDavMoveCopy.HandleMoveAsync(_lockStore, context, services, db, user, segments); break;
            case "COPY": await WebDavMoveCopy.HandleCopyAsync(_lockStore, context, services, db, user, segments); break;
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
















































    internal static async Task<EffectiveRights> RightsAsync(IServiceProvider services, User user, Guid documentId) =>
        await services.GetRequiredService<IEffectiveRightsCalculator>().GetEffectiveRightsAsync(user.Id, documentId);





}
