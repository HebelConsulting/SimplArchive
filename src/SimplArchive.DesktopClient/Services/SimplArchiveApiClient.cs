using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

// Raised for an Api action that failed with a message worth showing the user (duplicate name, no permission).
// Base for a real error condition surfaced by SimplArchiveApiClient — carries a user-facing message the crash
// guard / status line displays. No longer sealed: intent-named conditions subclass it (per CLAUDE.md's
// exception-type rule) while the existing catch(ApiActionException) surfacing still picks them up.
public class ApiActionException(string message) : Exception(message);

// Set-primary-location / promote-a-reference errors (ADR 0506) — a small family under ApiActionException so a
// caller can catch the whole group and the status line still shows the message. Each type fixes its own message.
public class PrimaryLocationException(string message) : ApiActionException(message);

public sealed class CannotSetPrimaryLocationException()
    : PrimaryLocationException("Can't set that folder as the primary location.");

public sealed class SetPrimaryLocationForbiddenException()
    : PrimaryLocationException("You don't have permission to change this item's primary location.");

public sealed class PrimaryLocationConcurrencyException()
    : PrimaryLocationException("This item changed since you loaded it — refresh and try again.");

// A dropped file whose name is already used in the target folder. Its own type rather than the string-message
// ApiActionException it replaces, because this one condition is RECOVERABLE — the caller asks the user what they
// meant (a new version of what is there, or a new document under another name) instead of only reporting it.
// Carries the name so the prompt can name the file without re-deriving it.
public sealed class DocumentNameTakenException(string fileName)
    : ApiActionException($"'{fileName}': a document with that name already exists here.")
{
    public string FileName { get; } = fileName;
}

// Raised by DeleteUserAsync when the user still holds pending review tasks and no replacement reviewer was
// supplied (ADR "Workflow review reassignment") — the caller (Users & groups tab) prompts for a replacement
// and retries with reassignReviewsTo.
public sealed class ReviewerHasPendingReviewsException(string message) : Exception(message);

// Thin HTTP client over the SimplArchive Api (the same endpoints the Blazor client uses). See ADR
// "Cross-platform desktop fat client (Avalonia)" and "Desktop workbench UI".
public sealed class SimplArchiveApiClient
{
    // internal: InboxApi PUTs bytes to a presigned URL too, since the inbox calls moved there (#443).
    internal static HttpClient Anonymous => ApiCore.Anonymous;
    private readonly HttpClient _http;
    private InboxApi? _inbox;
    private CheckoutClient? _checkout;
    private SearchClient? _search;
    private AdminClient? _admin;
    private DocumentsClient? _documents;
    private WorkflowClient? _workflow;
    private NotificationsClient? _notifications;
    private RemindersClient? _reminders;
    private AnnotationsClient? _annotations;

    public SimplArchiveApiClient(string accessToken)
    {
        Core = new ApiCore(accessToken);
        _http = Core.Http;
    }

    /// <summary>The shared authenticated HTTP core every per-area client rides on (#443).</summary>
    public ApiCore Core { get; }

    /// <summary>The check-out area (#443 tranche 1).</summary>
    public CheckoutClient Checkout => _checkout ??= new CheckoutClient(Core);

    /// <summary>The search area (#443 tranche 3).</summary>
    public SearchClient Search => _search ??= new SearchClient(Core);

    /// <summary>The administration area (#443 tranche 4).</summary>
    public AdminClient Admin => _admin ??= new AdminClient(Core);

    /// <summary>The documents area (#443, the finale).</summary>
    public DocumentsClient Documents => _documents ??= new DocumentsClient(Core, () => Reminders);

    /// <summary>The workflow area (#443 tranche 5).</summary>
    public WorkflowClient Workflow => _workflow ??= new WorkflowClient(Core);

    /// <summary>The notifications area (#443 tranche 5).</summary>
    public NotificationsClient Notifications => _notifications ??= new NotificationsClient(Core);

    /// <summary>The reminders & subscriptions area (#443 tranche 5).</summary>
    public RemindersClient Reminders => _reminders ??= new RemindersClient(Core);

    /// <summary>The annotations area (#443 tranche 5).</summary>
    public AnnotationsClient Annotations => _annotations ??= new AnnotationsClient(Core);

    // This client's bearer token — used as the RFC 8693 subject_token to start impersonation (ADR "User
    // impersonation").
    public string AccessToken => Core.AccessToken;

    // Exchanges an admin's access token for an impersonation token representing the target user (ADR "User
    // impersonation"); null if the exchange is refused (e.g. the target is an admin).
    public static async Task<string?> ExchangeImpersonationTokenAsync(string subjectToken, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(DesktopClientOptions.ApiBaseUrl) };
        var response = await http.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = "simplarchive-desktop",
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_subject"] = targetUserId.ToString(),
        }), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("access_token").GetString();
    }

    public sealed record OcrLanguageOption(string Code, string DisplayName);

    // FileExtension is the current version's derived extension (ADR "Extension off Document.Name"); native
    // Open/Save-as append it to Document.Name (the bare stem) to reconstruct a correct filename.
    public sealed record Preview(string? PreviewUrl, bool PreviewConverted, string? DownloadUrl, string? TextLayoutUrl, string? PreviewPagesUrl, string FileExtension, string? AnnotationsUrl = null);

    // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). Coordinates are normalized 0..1
    // within each page (top-left origin); the client scales them to the rendered page size.
    public sealed record TextLayoutBox(string Text, double X, double Y, double Width, double Height);

    public sealed record TextLayoutPageInfo(IReadOnlyList<TextLayoutBox> Words);

    public sealed record TextLayoutInfo(IReadOnlyList<TextLayoutPageInfo> Pages);

    public sealed record UserCard(string DisplayName, string Email, bool IsActive, string? PhotoHref);

    // The signed-in principal's ids + display names (ADR "S3-backed inbox") — names drive the local folder
    // path. IsTenantAdmin gates admin-only actions (e.g. the searchable-PDF backfill).
    public sealed record WhoAmIInfo(Guid? UserId, Guid? TenantId, string? TenantName, string? UserName, bool IsTenantAdmin, bool CanManageUsers, bool HasPhoto, bool CanViewAuditLog, bool MfaEnabled, bool CanResetMfa, bool CanLegalHold, bool CanManageClassification, bool CanOverrideCheckout = false, bool CanImpersonate = false, string? ImpersonatedBy = null, bool CanExport = false, bool CanImport = false, bool CanManageInboxes = false, bool CanManageServiceAccounts = false);
    // A user option for the reviewer picker.
    // RemoveHref is set only where the option came from a collection whose rows advertise a removal address —
    // a group's members; it is null for pickers such as reminder targets (issue #416).
    public sealed record UserOptionInfo(Guid Id, string DisplayName, string? RemoveHref = null);

    // Audit log (ADRs "Audit trail (first slice)" / "... hash chain" / "... retention and purge").
    public sealed record AuditEventInfo(DateTimeOffset Timestamp, string ActorType, string ActorName, string Action, string? TargetType, string? TargetName, string? Details);
    public sealed record AuditPage(IReadOnlyList<AuditEventInfo> Events, string? NextCursor);
    public sealed record AuditVerifyInfo(bool Valid, int CheckedCount, long? BrokenAtSequence);
    public sealed record AuditRetentionInfo(int RetentionDays, long ChainStartSequence, DateTimeOffset? LastPurgedAt);
    public sealed record AuditPurgeInfo(int PurgedCount, long ChainStartSequence);

    // A page of audit events, newest first, with optional filters + an opaque cursor for "load more".
    public async Task<AuditPage> GetAuditEventsAsync(string? action, DateTimeOffset? from, DateTimeOffset? to, string? cursor, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.UtcDateTime.ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.UtcDateTime.ToString("o"))}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");

        var url = await RootHrefAsync("auditEvents", cancellationToken) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var page = await _http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var events = page.TryGetProperty("events", out var array)
            ? array.EnumerateArray().Select(ParseAuditEvent).ToList()
            : new List<AuditEventInfo>();
        return new AuditPage(events, ExtractCursor(FindLink(page, "next")));
    }

    // Exports the tenant audit log (respecting the filters) as NDJSON bytes (ADR "Audit trail export").
    public async Task<byte[]> ExportAuditEventsAsync(string? action, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.UtcDateTime.ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.UtcDateTime.ToString("o"))}");

        var url = await AuditRelAsync("export", cancellationToken) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return await _http.GetByteArrayAsync(url, cancellationToken);
    }

    public async Task<AuditVerifyInfo> VerifyAuditChainAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await AuditRelAsync("verify", cancellationToken), cancellationToken);
        return new AuditVerifyInfo(
            json.GetProperty("valid").GetBoolean(),
            json.GetProperty("checkedCount").GetInt32(),
            json.TryGetProperty("brokenAtSequence", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null);
    }

    public sealed record WormVerifyInfo(bool Valid, int SegmentCount, int CheckedCount, long? BrokenAtSequence, string? Reason);

    // Verifies the sealed WORM segments against the DB (ADR "Audit WORM segment verify").
    public async Task<WormVerifyInfo> VerifyAuditWormAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await AuditRelAsync("worm-verify", cancellationToken), cancellationToken);
        return new WormVerifyInfo(
            json.GetProperty("valid").GetBoolean(),
            json.GetProperty("segmentCount").GetInt32(),
            json.GetProperty("checkedCount").GetInt32(),
            json.TryGetProperty("brokenAtSequence", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null,
            json.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null);
    }

    // Retention, export, verify, worm-verify and purge are all rels ON the audit-events collection (issue
    // #416), so reaching any of them means reading that collection — and pulling a page of audit events to
    // learn one address is the "two round trips, one of them large" trap. `?limit=1` on the advertised href
    // keeps the read trivial while the address still comes from the server: a query on a rel's href, not a
    // path this client invented.
    //
    // Cached like the API root's own rels, and for the same reason: these five do not change between calls,
    // and the audit tab would otherwise re-read the collection once per button.
    private async Task<string> AuditRelAsync(string rel, CancellationToken cancellationToken)
    {
        if (_auditLinks is null)
        {
            await _auditGate.WaitAsync(cancellationToken);
            try
            {
                if (_auditLinks is null)
                {
                    var href = await RootHrefAsync("auditEvents", cancellationToken);
                    var page = await _http.GetFromJsonAsync<JsonElement>($"{href}?limit=1", cancellationToken);
                    _auditLinks = ParseLinks(page) ?? new Dictionary<string, string>();
                }
            }
            finally
            {
                _auditGate.Release();
            }
        }

        return _auditLinks.TryGetValue(rel, out var relHref)
            ? relHref
            : throw new InvalidOperationException($"The audit log advertised no '{rel}' rel (ADR 0543).");
    }

    private IReadOnlyDictionary<string, string>? _auditLinks;
    private readonly SemaphoreSlim _auditGate = new(1, 1);

    private Task<string> AuditRetentionHrefAsync(CancellationToken cancellationToken) =>
        AuditRelAsync("retention", cancellationToken);

    public async Task<AuditRetentionInfo> GetAuditRetentionAsync(CancellationToken cancellationToken = default) =>
        ParseRetention(await _http.GetFromJsonAsync<JsonElement>(await AuditRetentionHrefAsync(cancellationToken), cancellationToken));

    public async Task<AuditRetentionInfo> SetAuditRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(await AuditRetentionHrefAsync(cancellationToken), new { retentionDays }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to change audit retention.");
        }

        response.EnsureSuccessStatusCode();
        return ParseRetention(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task<AuditPurgeInfo> PurgeAuditAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await AuditRelAsync("purge", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to purge the audit log.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new AuditPurgeInfo(json.GetProperty("purgedCount").GetInt32(), json.GetProperty("chainStartSequence").GetInt64());
    }

    private static AuditEventInfo ParseAuditEvent(JsonElement e) => new(
        e.GetProperty("timestamp").GetDateTimeOffset(),
        e.TryGetProperty("actorType", out var at) ? at.GetString() ?? "" : "",
        e.TryGetProperty("actorName", out var an) ? an.GetString() ?? "" : "",
        e.TryGetProperty("action", out var ac) ? ac.GetString() ?? "" : "",
        StrOrNull(e, "targetType"),
        StrOrNull(e, "targetName"),
        StrOrNull(e, "details"));

    private static AuditRetentionInfo ParseRetention(JsonElement json) => new(
        json.GetProperty("retentionDays").GetInt32(),
        json.GetProperty("chainStartSequence").GetInt64(),
        json.TryGetProperty("lastPurgedAt", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetDateTimeOffset() : null);

    // Pulls the cursor value out of a "next" hypermedia href (…?cursor=…&limit=…).
    private static string? ExtractCursor(string? nextHref)
    {
        if (string.IsNullOrEmpty(nextHref))
        {
            return null;
        }

        var index = nextHref.IndexOf("cursor=", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var value = nextHref[(index + "cursor=".Length)..];
        var amp = value.IndexOf('&');
        return Uri.UnescapeDataString(amp >= 0 ? value[..amp] : value);
    }

    public sealed record AdminPersonalRepoInfo(Guid UserId, string DisplayName, string Email, bool UserIsActive, Guid RepositoryId, bool HasChildren, bool HasSubfolders);

    // Lists every user's personal repository (ADR "Tenant-admin Administration → Users view") — tenant-admin only.
    public async Task<List<AdminPersonalRepoInfo>> GetAdminPersonalRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        // The root's `admin` rel leads to the administration index, which advertises this list — two hops, but
        // both of them followed rather than assembled, and paid once per admin screen (ADR 0543).
        var admin = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("admin", cancellationToken), cancellationToken);
        var json = await _http.GetFromJsonAsync<JsonElement>(ApiCore.RequireRel(admin, "personal-repositories", "The administration index"), cancellationToken);
        var list = new List<AdminPersonalRepoInfo>();
        if (json.TryGetProperty("repositories", out var array))
        {
            foreach (var r in array.EnumerateArray())
            {
                list.Add(new AdminPersonalRepoInfo(
                    r.GetProperty("userId").GetGuid(),
                    r.GetProperty("displayName").GetString() ?? "",
                    r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    r.TryGetProperty("userIsActive", out var a) && a.GetBoolean(),
                    r.GetProperty("repositoryId").GetGuid(),
                    r.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
                    r.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean()));
            }
        }
        return list;
    }

    // Get-or-create the current user's personal repository (ADR "Per-user personal repository"). Returns null if
    // the caller has no personal space (e.g. a ServiceAccount → 403) so the tree still renders shared repositories.
    public async Task<Node?> GetPersonalRepositoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await MeHrefAsync("personalRepository", cancellationToken), null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new Node(
            json.GetProperty("id").GetGuid(),
            json.GetProperty("name").GetString() ?? "Personal",
            json.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
            HasVersions: false,
            json.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
            HasReferences: false,
            // The resource advertises `children` — carry it, or the Personal tree node has no address to expand
            // by and Href() throws (ADR 0543). Hand-built Nodes are exactly where this is easy to forget, which
            // is what DesktopListingRelsTests now guards.
            Links: ParseLinks(json));
    }
    /// <summary>The essentials of a document reached by ADDRESS — what a cross-tab open needs in one read.</summary>
    public sealed record DocumentStub(Guid Id, string Name, IReadOnlyDictionary<string, string> Links);

    /// <summary>
    /// A document by its ADVERTISED address (#443): id, name and rels from one GET. This is what a payload-row
    /// consumer (a task, a notification, a reminder, a search hit) uses to open the folder its row named —
    /// following the row's `parent`/`document` rel instead of handing a bare id back into the address turn.
    /// One request where the id path cost two (a name fetch plus a links fetch).
    /// </summary>
    public async Task<DocumentStub> GetDocumentByAddressAsync(string documentHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(documentHref, cancellationToken);
        return new DocumentStub(
            json.GetProperty("id").GetGuid(),
            json.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            ParseLinks(json) ?? throw new InvalidOperationException($"'{documentHref}' advertised no links at all (ADR 0543)."));
    }

    // Downloads one archive entry's bytes at the address its row advertised (ADR 0543/0555).
    public Task<byte[]> DownloadArchiveEntryAsync(string downloadHref, CancellationToken cancellationToken = default) =>
        _http.GetByteArrayAsync(downloadHref, cancellationToken);

    public async Task<WhoAmIInfo> GetWhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("whoami", cancellationToken), cancellationToken);
        return new WhoAmIInfo(
            json.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String ? u.GetGuid() : null,
            json.TryGetProperty("tenantId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetGuid() : null,
            json.TryGetProperty("tenantName", out var tn) ? tn.GetString() : null,
            json.TryGetProperty("userName", out var un) ? un.GetString() : null,
            json.TryGetProperty("isTenantAdmin", out var a) && a.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageUsers", out var m) && m.ValueKind == JsonValueKind.True,
            json.TryGetProperty("hasPhoto", out var hp) && hp.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canViewAuditLog", out var av) && av.ValueKind == JsonValueKind.True,
            json.TryGetProperty("mfaEnabled", out var mfa) && mfa.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canResetMfa", out var crm) && crm.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canLegalHold", out var clh) && clh.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageClassification", out var cmc) && cmc.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canOverrideCheckout", out var coc) && coc.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canImpersonate", out var ci) && ci.ValueKind == JsonValueKind.True,
            json.TryGetProperty("impersonatedBy", out var ib) && ib.ValueKind == JsonValueKind.String ? ib.GetString() : null,
            json.TryGetProperty("canExport", out var ce) && ce.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canImport", out var cim) && cim.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageInboxes", out var cmi) && cmi.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageServiceAccounts", out var cmsa) && cmsa.ValueKind == JsonValueKind.True);
    }

    // The count of existing "current TIFF" documents with no searchable-PDF successor yet (ADR "Backfill
    // searchable PDFs for existing TIFFs").
    public async Task<int> GetTiffBackfillPendingAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("searchablePdfBackfill", cancellationToken), cancellationToken);
        return json.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    }

    // Enqueues a searchable-PDF conversion for every current TIFF; returns how many were enqueued.
    public async Task<int> TriggerTiffBackfillAsync(CancellationToken cancellationToken = default)
    {
        // Same address as the status read above — the root advertises it once and both verbs follow it.
        var response = await _http.PostAsync(await RootHrefAsync("searchablePdfBackfill", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    }

    /// <summary>The inbox's own api surface (ADR 0575) — its listing and its page operations.</summary>
    public InboxApi Inbox => _inbox ??= new InboxApi(Core);

    // Turns a failed response into an ApiActionException carrying text the USER can read, in their language.
    //
    // Reads `errorCode`, not `detail`. The detail is English — the API's 153 exception classes carry their
    // message as a constructor literal, so no Accept-Language handling reaches them — and this method is on the
    // path of every failed call in the desktop, which made it the single biggest source of English in an
    // otherwise German UI (issue #424). The code is the stable, language-neutral contract (ADR 0543), so it
    // crosses the wire and ApiErrorText supplies the words.
    internal static async Task ThrowIfProblemAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? errorCode = null;
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (json.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code)
            {
                errorCode = code;
            }
        }
        catch
        {
            // No problem body at all (a proxy error page, a connection reset) — fall back to the caller's message,
            // which is already localised at its call site.
            throw new ApiActionException(fallback);
        }

        throw new ApiActionException(errorCode is null ? fallback : ApiErrorText.For(errorCode));
    }

    // The always-shown system fields (ADR "System fields + OCR-language mask field"): Created/CreatedBy/
    // DocumentDate from the latest confirmed version; the OCR-language override + whether a TIFF source exists
    // from the latest confirmed TIFF version.
    // The document's current version JsonElement honoring the server's currentVersionId pointer (ADR
    // "Version-restore via a current-version pointer", issue #265), else the latest confirmed. Returns the
    // element + its version number, or null when there's no confirmed version.
    internal static (JsonElement Version, int Number)? PickCurrentVersionElement(JsonElement response)
    {
        if (!response.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Guid? pointer = response.TryGetProperty("currentVersionId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetGuid() : null;
        JsonElement? latest = null, pinned = null;
        int latestNumber = -1, pinnedNumber = -1;
        foreach (var v in versions.EnumerateArray())
        {
            if (v.GetProperty("status").GetString() != "Confirmed")
            {
                continue;
            }

            var number = v.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : 0;
            if (number >= latestNumber) { latestNumber = number; latest = v; }
            if (pointer is { } p && v.GetProperty("id").GetGuid() == p) { pinned = v; pinnedNumber = number; }
        }

        if (pinned is { } pv) return (pv, pinnedNumber);
        if (latest is { } lv) return (lv, latestNumber);
        return null;
    }

    public async Task<IReadOnlyList<OcrLanguageOption>> GetOcrLanguageCatalogAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("ocrLanguages", cancellationToken), cancellationToken);
        var list = new List<OcrLanguageOption>();
        if (json.TryGetProperty("languages", out var langs))
        {
            foreach (var l in langs.EnumerateArray())
            {
                list.Add(new OcrLanguageOption(l.GetProperty("code").GetString() ?? "", l.GetProperty("displayName").GetString() ?? ""));
            }
        }

        return list;
    }

    public async Task CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
    {
        var resp = await _http.PostAsJsonAsync(await RootHrefAsync("tags", cancellationToken), new { name, color }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await ErrorMessageAsync(resp, "Could not add the tag."));
    }

    // As ThrowIfProblemAsync: the machine code, never the server's English `detail` (issue #424).
    internal static async Task<string> ErrorMessageAsync(HttpResponseMessage resp, string fallback)
    {
        try
        {
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code) return ApiErrorText.For(code);
        }
        catch { /* not a problem+json body */ }

        return fallback;
    }

    public sealed record DashFollowedInfo(Guid DocumentId, Guid? ParentId, string DocumentName, IReadOnlyDictionary<string, string>? Links = null);

    // The documents the caller follows (the dashboard's Following section).
    public async Task<IReadOnlyList<DashFollowedInfo>> GetDashboardFollowingAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("subscriptions", cancellationToken), cancellationToken);
        var list = new List<DashFollowedInfo>();
        if (json.TryGetProperty("followed", out var arr))
        {
            foreach (var f in arr.EnumerateArray())
            {
                list.Add(new DashFollowedInfo(
                    f.GetProperty("documentId").GetGuid(),
                    f.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                    f.GetProperty("documentName").GetString() ?? "",
                    ParseLinks(f)));
            }
        }

        return list;
    }

    // Sets a version's document (issuing) date ("yyyy-MM-dd") at the address the version row advertised.
    public async Task SetDocumentDateAsync(string documentDateHref, string documentDate, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync(documentDateHref, new { documentDate }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the document date ({(int)response.StatusCode}).");
        }
    }

    // A preview from an ALREADY-ADVERTISED versions address, for a caller holding a row rather than an id
    // (#462). Same body as GetPreviewAsync below the first line; that one has to turn an id back into the
    // address first, which is the round trip a row-holder should not pay (ADR 0557).
    public async Task<Preview> GetPreviewFromVersionsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(versionsHref.TrimStart('/'), cancellationToken);
        if (PickCurrentVersionElement(response) is not { } picked)
        {
            return new Preview(null, false, null, null, null, "");
        }

        var confirmed = picked.Version;
        var converted = confirmed.TryGetProperty("previewConverted", out var pc) && pc.GetBoolean();
        var extension = confirmed.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "";
        return new Preview(FindLink(confirmed, "preview"), converted, FindLink(confirmed, "download"), FindLink(confirmed, "text-layout"), FindLink(confirmed, "preview-pages"), extension, FindLink(confirmed, "annotations"));
    }

    // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"); null (204) for
    // every other format, where the caller uses the single preview URL.
    public async Task<IReadOnlyList<string>?> GetPreviewPagesAsync(string previewPagesUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(previewPagesUrl.TrimStart('/'), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var urls = new List<string>();
        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("url", out var url) && url.GetString() is { } u)
            {
                urls.Add(u);
            }
        }

        return urls.Count > 0 ? urls : null;
    }

    // Fetches the per-page word boxes for hit-overlay (ADR "Search hit overlay"). textLayoutUrl is the version
    // resource's `text-layout` link; a 204 (unsupported format / nothing recognized) yields null.
    public async Task<TextLayoutInfo?> GetTextLayoutAsync(string textLayoutUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(textLayoutUrl.TrimStart('/'), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var pageList = new List<TextLayoutPageInfo>();
        foreach (var page in pages.EnumerateArray())
        {
            var words = new List<TextLayoutBox>();
            if (page.TryGetProperty("words", out var wordArray) && wordArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wordArray.EnumerateArray())
                {
                    words.Add(new TextLayoutBox(
                        w.GetProperty("text").GetString() ?? "",
                        w.GetProperty("x").GetDouble(),
                        w.GetProperty("y").GetDouble(),
                        w.GetProperty("width").GetDouble(),
                        w.GetProperty("height").GetDouble()));
                }
            }

            pageList.Add(new TextLayoutPageInfo(words));
        }

        return new TextLayoutInfo(pageList);
    }

    public string? GetDownloadUrl(Preview preview) => preview.DownloadUrl;
    internal static DateTimeOffset? OptDate(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;

    // One soft-deleted document in the tenant-wide recycle bin (ADR "Recycle bin tab" / "Desktop recycle bin
    // parity"): its name, full path, when it was deleted, and by whom (from the audit trail).
    // A soft-deleted document. Its own `restore`/`purge` addresses come from the ROW, because the document is
    // behind the soft-delete query filter — there is no resource left to fetch them from (ADR 0543/0555).
    public sealed record RecycleBinEntry(Guid Id, string Name, string Path, DateTimeOffset DeletedAt, string DeletedBy,
        IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // The bin plus what can be done to it as a whole — captured where the collection is read, so the tab does
    // not pay a request per button (ADR 0557).
    public sealed record RecycleBinList(IReadOnlyList<RecycleBinEntry> Items, IReadOnlyDictionary<string, string> Links);

    // Every soft-deleted document the caller can see, tenant-wide (ADR "Recycle bin tab") — capped at 500 by the
    // Api (Truncated flag ignored here; the tab tells the user if more exist via the status line).
    public async Task<RecycleBinList> GetRecycleBinItemsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("recycleBin", cancellationToken), cancellationToken);
        var items = new List<RecycleBinEntry>();
        if (response.TryGetProperty("items", out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                items.Add(new RecycleBinEntry(
                    item.GetProperty("id").GetGuid(),
                    item.GetProperty("name").GetString() ?? "",
                    item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    item.GetProperty("deletedAt").GetDateTimeOffset(),
                    item.TryGetProperty("deletedBy", out var db) ? db.GetString() ?? "—" : "—",
                    ParseLinks(item)));
            }
        }

        return new RecycleBinList(items, ParseLinks(response) ?? new Dictionary<string, string>());
    }

    // Empties the whole tenant-wide recycle bin — permanently purges every soft-deleted document (ADR "Recycle
    // bin tab") — tenant-admin only.
    public async Task PurgeRecycleBinAsync(string purgeAllHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(purgeAllHref, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can empty the recycle bin.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Bulk restore (ADR "Bulk restore from the recycle bin") — restores each requested soft-deleted document +
    // its subtree in one call; returns how many were restored vs skipped (already active / gone / not permitted).
    public async Task<(int Restored, int Skipped)> RestoreManyAsync(string restoreSelectedHref, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(restoreSelectedHref, new { ids }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("restored").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    // Bulk purge of selected items (ADR "Bulk purge of selected recycle-bin items") — tenant-admin; permanently
    // removes each requested recycle-bin root + subtree; returns purged vs skipped (gone / active / held / WORM).
    public async Task<(int Purged, int Skipped)> PurgeManyAsync(string purgeSelectedHref, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(purgeSelectedHref, new { ids }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can purge items.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("purged").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    // ---- Version comparison (ADR "Document version comparison") ----
    // A version row, carrying the links its own row advertised — `restore` and `document-date` are followed
    // from here rather than rebuilt from a document id and a version id (ADR 0543/0555).

    public sealed record VersionInfo(Guid Id, int? VersionNumber, string Status, string FileExtension, string? DownloadUrl,
        string DocumentDate = "", DateTimeOffset CreatedAt = default, string CreatedByName = "", bool IsCurrent = false,
        string? Comment = null, IReadOnlyDictionary<string, string>? Links = null, string? WorkflowStatus = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    public sealed record DiffLineInfo(int Op, string Text);



    // Inline unified diff of a checked-out document's current version vs its working copy in check-out (ADR 0517).
    // Holder-only; Available=false when there's no working-copy stash or a side has no extractable text.

    public sealed record VersionComparison(bool Available, List<DiffLineInfo> Lines);

    // Restores (rolls back to) an earlier version (ADR "Version restore") — creates a new current version from
    // its content. Throws on a rejected request (403 no edit rights, 409 workflow/hold/checkout).
    public async Task RestoreVersionAsync(VersionInfo version, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(version, "restore"), null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode == HttpStatusCode.Conflict
                ? "the document is under a workflow, legal hold, or checked out"
                : $"HTTP {(int)response.StatusCode}";
            throw new ApiActionException($"Could not restore this version ({reason}).");
        }
    }

    // The confirmed versions of a document (newest first), each with its presigned download URL.
    // Takes the advertised href (node.Href("versions")), not a document id (ADR 0543, issue #416).
    /// <summary>
    /// The version list plus the collection's own `compare` address — one read, so a screen that offers
    /// comparison does not pay a second request to learn where to send it (issue #416).
    /// </summary>
    public async Task<(List<VersionInfo> Versions, string? CompareHref)> GetVersionsWithLinksAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        var compareHref = ParseLinks(json) is { } links && links.TryGetValue("compare", out var href) ? href : null;
        return (await GetVersionsAsync(versionsHref, cancellationToken), compareHref);
    }

    public async Task<List<VersionInfo>> GetVersionsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        var list = new List<VersionInfo>();
        if (json.TryGetProperty("versions", out var arr))
        {
            foreach (var v in arr.EnumerateArray())
            {
                string? download = null;
                if (v.TryGetProperty("links", out var links))
                {
                    foreach (var l in links.EnumerateArray())
                    {
                        if (l.GetProperty("rel").GetString() == "download") { download = l.GetProperty("href").GetString(); }
                    }
                }

                list.Add(new VersionInfo(
                    v.GetProperty("id").GetGuid(),
                    v.TryGetProperty("versionNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null,
                    v.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                    v.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "",
                    download,
                    v.TryGetProperty("documentDate", out var dd) ? dd.GetString() ?? "" : "",
                    v.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetDateTimeOffset() : default,
                    v.TryGetProperty("createdByName", out var cb) ? cb.GetString() ?? "" : "",
                    Comment: v.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() : null,
                    Links: ParseLinks(v), WorkflowStatus: StrOrNull(v, "workflowStatus")));
            }
        }

        // Flag the current version = the server's CurrentVersionId pointer (issue #265), else the latest confirmed.
        Guid? pointer = json.TryGetProperty("currentVersionId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetGuid() : null;
        var confirmed = list.Where(v => v.Status == "Confirmed").OrderByDescending(v => v.VersionNumber ?? 0).ToList();
        var currentId = pointer ?? confirmed.FirstOrDefault()?.Id;
        return confirmed.Select(v => v with { IsCurrent = v.Id == currentId }).ToList();
    }

    // Takes the version collection's advertised `compare` address; the two versions travel as query parameters,
    // because a link names ONE resource and a pair has none (issue #416, resolved by reshaping the API).
    public async Task<VersionComparison> GetVersionComparisonAsync(string compareHref, Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>($"{compareHref}?from={fromVersionId}&to={toVersionId}", cancellationToken);
        var lines = new List<DiffLineInfo>();
        if (json.TryGetProperty("lines", out var arr))
        {
            foreach (var l in arr.EnumerateArray())
            {
                lines.Add(new DiffLineInfo(l.GetProperty("op").GetInt32(), l.GetProperty("text").GetString() ?? ""));
            }
        }

        return new VersionComparison(json.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True, lines);
    }


    // A specific version's bytes (via its presigned download URL) — used to stage both versions to temp files for
    // an external diff tool (Beyond Compare).
    public async Task<byte[]> DownloadVersionBytesAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        var (bytes, _) = await DownloadAsync(downloadUrl, cancellationToken);
        return bytes;
    }

    // Fetches a preview/download URL's bytes (a presigned URL — no auth) plus its content-type.
    public static async Task<(byte[] Bytes, string ContentType)> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await Anonymous.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    private Task<List<T>> LoadPagedAsync<T>(string url, string arrayProperty, Func<JsonElement, T> parse, CancellationToken cancellationToken,
        Action<JsonElement>? onPage = null) => Core.LoadPagedAsync(url, arrayProperty, parse, cancellationToken, onPage);

    // rel -> href for one resource's advertised links, relative (the HttpClient has the base address).
    internal static IReadOnlyDictionary<string, string>? ParseLinks(JsonElement item)
    {
        if (!item.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in links.EnumerateArray())
        {
            if (l.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } r
                && l.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
            {
                map[r] = h.TrimStart('/');
            }
        }

        return map.Count == 0 ? null : map;
    }

    // The root document, fetched once per client instance. Cached because the root is a constant for a session:
    // re-reading it before every call would turn one request into two, which is the usual reason a codebase
    // abandons hypermedia and goes back to composing paths (issue #416).

    // The caller's own "me" resource, cached for the same reason as the root. Everything about the signed-in
    // account hangs off it — password, photo, MFA, passkeys, WebDAV password, personal repository, notification
    // preferences — so this is the desktop's counterpart to the web client's MeHrefAsync (issue #416). Without
    // it every one of those was a composed /api/users/me/… path, which is thirteen private routes copied into a
    // second codebase.
    private IReadOnlyDictionary<string, string>? _meLinks;
    private string? _myEmail;
    private readonly SemaphoreSlim _meGate = new(1, 1);

    /// <summary>
    /// The caller's own email address, or <c>null</c> for a principal with no personal account.
    /// </summary>
    /// <remarks>
    /// Comes from the same "me" read the rels do, so a profile screen showing who you are signed in as costs no
    /// request of its own (#464).
    /// </remarks>
    public async Task<string?> MyEmailAsync(CancellationToken cancellationToken = default)
    {
        // Any rel will do: resolving one populates the whole document, email included.
        await MeHrefAsync("self", cancellationToken);
        return _myEmail;
    }

    /// <summary>
    /// The href for a rel on the caller's own "me" resource. Throws when it is not advertised.
    /// </summary>
    public async Task<string> MeHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_meLinks is null)
        {
            // Resolve the root href BEFORE taking the gate: these are separate semaphores, but taking one while
            // holding the other is how the web client deadlocked its whole workbench (ADR 0543 notes), so keep
            // the acquisition order trivially safe by not nesting at all.
            var meHref = await RootHrefAsync("me", cancellationToken);

            await _meGate.WaitAsync(cancellationToken);
            try
            {
                if (_meLinks is null)
                {
                    var me = await _http.GetFromJsonAsync<JsonElement>(meHref, cancellationToken);
                    _meLinks = ParseLinks(me) ?? new Dictionary<string, string>();

                    // The email rides in the SAME response as the links (#464) — reading it here rather than
                    // adding a second call is ADR 0557's rule applied to a value, not an address: one read,
                    // everything it carried.
                    _myEmail = me.TryGetProperty("email", out var email) && email.ValueKind is JsonValueKind.String
                        ? email.GetString()
                        : null;
                }
            }
            finally
            {
                _meGate.Release();
            }
        }

        return _meLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The 'me' resource does not advertise the '{rel}' rel.");
    }

    /// <summary>
    /// The href for a root-level rel. Throws when the server does not advertise it — for the collections a screen
    /// is built around, a null would surface as an empty list ("you have no tags") rather than as a fault.
    /// </summary>
    public Task<string> RootHrefAsync(string rel, CancellationToken cancellationToken = default) =>
        Core.RootHrefAsync(rel, cancellationToken);

    // The API root's link relations — the ONE URL a client is allowed to know (ADR 0543); everything else is
    // discovered from here. Note "api" carries no slash, so it is not a composed resource path.
    public async Task<IReadOnlyDictionary<string, string>> GetRootLinksAsync(CancellationToken cancellationToken = default)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        using var response = await _http.GetAsync("api", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return links;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("links", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in items.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } name
                    && link.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } value)
                {
                    links[name] = value.TrimStart('/');
                }
            }
        }

        return links;
    }

    // ---- External links (ADR 0546) ---------------------------------------------------------------------

    public sealed record ExternalLinkInfo(
        Guid Id, Guid DocumentId, string DocumentName, string? Url, DateTimeOffset ExpiresAt,
        int? MaxAccesses, int AccessCount, string CreatedByName, bool CanExtend, string Etag,
        string? RevokeHref, string? AvailabilityHref, Guid? ParentId, string? RevealUrlHref = null)
    {
        /// <summary>
        /// The expiry as the READER experiences it. The server sends UTC; the row used to format that raw while
        /// the detail dialog called ToLocalTime(), so one link showed two times an hour apart outside UTC — the
        /// list said 20:40 and its own detail said 21:40 (the bug report this fixes). One property now, used by
        /// both, so they cannot drift again.
        /// </summary>
        public string ExpiresLocal => ExpiresAt.ToLocalTime().ToString("g");
    }

    public sealed record ExternalLinkListInfo(IReadOnlyList<ExternalLinkInfo> Links, bool CanCreate, bool CanViewOthers);

    // Follows the href the document resource advertised via its "external-links" rel (ADR 0543) — never composed.
    public async Task<ExternalLinkListInfo> GetExternalLinksAsync(string linksHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(linksHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ExternalLinkListInfo([], false, false);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseLinkList(doc.RootElement);
    }

    // The caller's own links across all documents; a tenant admin may filter by user or group.
    public async Task<ExternalLinkListInfo> GetMyExternalLinksAsync(
        string linksHref, Guid? userId = null, Guid? groupId = null, CancellationToken cancellationToken = default)
    {
        var query = groupId is { } g ? $"?groupId={g}" : userId is { } u ? $"?userId={u}" : "";
        return await GetExternalLinksAsync(linksHref + query, cancellationToken);
    }

    // Returns the created link — the ONLY time its URL is available, since the list endpoints never return the
    // token (ADR 0546). Null when the tenant has the feature switched off or the caller lacks the right.
    // Returns null ONLY when the share was refused — the tenant switch is off, or the caller lacks the right.
    // Anything else throws, so the dialog reports a real failure as one. Collapsing every non-success into null
    // is what made a 500 (a non-UTC expiry Postgres refused to store) display as "external links are switched off
    // for this tenant": a message that sent the reader to a setting that was already correct.
    public async Task<ExternalLinkInfo?> CreateExternalLinkAsync(
        string linksHref, DateTimeOffset? expiresAt, int? maxAccesses, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(linksHref, new { expiresAt, maxAccesses }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return null;
            }

            throw new ApiActionException($"The link could not be created ({(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseLink(doc.RootElement);
    }

    public async Task<bool> RevokeExternalLinkAsync(string revokeHref, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, revokeHref);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // Days are measured from TODAY by the server, not added onto what remains, and the access cap travels in the
    // same call — a link out of both time and accesses is only half-renewed by moving either alone (ADR 0546).
    // maxAccesses null means unlimited; the server takes both in one request so they cannot land apart.
    public async Task<bool> RenewExternalLinkAsync(
        string availabilityHref, int days, int? maxAccesses, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, availabilityHref)
        {
            Content = JsonContent.Create(new { days, maxAccesses }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static ExternalLinkListInfo ParseLinkList(JsonElement root)
    {
        var links = new List<ExternalLinkInfo>();
        if (root.TryGetProperty("externalLinks", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            links.AddRange(items.EnumerateArray().Select(ParseLink));
        }

        return new ExternalLinkListInfo(
            links,
            root.TryGetProperty("canCreate", out var c) && c.ValueKind == JsonValueKind.True,
            root.TryGetProperty("canViewOthers", out var v) && v.ValueKind == JsonValueKind.True);
    }

    private static ExternalLinkInfo ParseLink(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.TryGetProperty("documentId", out var d) ? d.GetGuid() : Guid.Empty,
        item.TryGetProperty("documentName", out var dn) ? dn.GetString() ?? "" : "",
        item.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() : null,
        item.GetProperty("expiresAt").GetDateTimeOffset(),
        item.TryGetProperty("maxAccesses", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetInt32() : null,
        item.TryGetProperty("accessCount", out var a) ? a.GetInt32() : 0,
        item.TryGetProperty("createdByName", out var cb) ? cb.GetString() ?? "" : "",
        item.TryGetProperty("canExtend", out var ce) && ce.ValueKind == JsonValueKind.True,
        item.TryGetProperty("etag", out var e) ? e.GetString() ?? "" : "",
        ApiCore.RelHref(item, "revoke"),
        ApiCore.RelHref(item, "availability"),
        // Null in the per-document list, which is already sitting on the document — "Go to" only means something
        // in the cross-document one, where a row is the reader's only handle on where the thing lives.
        item.TryGetProperty("parentId", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.GetGuid() : null,
        // The tenant's opt-in to revealing an existing link's URL (issue #412), as the server states it: the rel
        // is advertised only where ShowExternalLinkUrl is on, so its ABSENCE is what makes "not shown" truthful
        // (ADR 0543). The desktop ignored it entirely and always claimed the URL was unavailable.
        ApiCore.RelHref(item, "reveal-url"));

    /// <summary>
    /// An existing link's URL, fetched on demand by FOLLOWING the row's advertised <c>reveal-url</c> (ADR 0543).
    /// </summary>
    /// <remarks>
    /// Deliberately not carried by the listing: the token travels only when somebody asks for this one link,
    /// which is what keeps it out of the page every row arrived on. Null when the fetch fails, so the caller
    /// leaves the note as it was rather than showing an empty "URL:".
    /// </remarks>
    public async Task<string?> RevealExternalLinkUrlAsync(string revealHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(revealHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    // Fetch an author's identity card by FOLLOWING the href the message advertised (ADR 0544).
    public async Task<(UserCard Card, byte[]? Photo)?> GetUserCardAsync(string cardHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(cardHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var card = new UserCard(
            root.GetProperty("displayName").GetString() ?? "",
            root.GetProperty("email").GetString() ?? "",
            root.TryGetProperty("isActive", out var active) && active.GetBoolean(),
            ApiCore.RelHref(root, "photo"));

        // The photo rel is present only when one exists, so this never probes for a 404. The endpoint is
        // bearer-protected, so the bytes must come through the authenticated client.
        byte[]? photo = null;
        if (card.PhotoHref is { } photoHref)
        {
            try
            {
                photo = await _http.GetByteArrayAsync(photoHref, cancellationToken);
            }
            catch (HttpRequestException)
            {
                photo = null;
            }
        }

        return (card, photo);
    }

    private static string RequireHref(LegalHoldInfo hold, string rel) =>
        hold.Href(rel)
        ?? throw new InvalidOperationException($"The legal hold '{hold.Name}' advertised no '{rel}' rel — a RELEASED hold offers neither release nor add-item (ADR 0543/0555).");

    private static string RequireHref(RetentionItemInfo item, string rel) =>
        item.Href(rel)
        ?? throw new InvalidOperationException($"'{item.DocumentName}' advertised no '{rel}' rel — a hold or a required review withholds it (ADR 0543/0555).");

    private static string RequireHref(VersionInfo version, string rel) =>
        version.Href(rel)
        ?? throw new InvalidOperationException($"Version {version.VersionNumber} advertised no '{rel}' rel — only a confirmed version offers one (ADR 0543/0555).");

    internal static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return json.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Passwords (ADR "User password management") -------------------------------------------------

    public async Task ChangeMyPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(await MeHrefAsync("changePassword", cancellationToken), new { currentPassword, newPassword }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("The current password is incorrect.");
        }

        response.EnsureSuccessStatusCode();
    }

    // WebDAV gateway (ADR "WebDAV gateway") — the app-specific WebDAV password + mount info.
    public sealed record WebDavStatus(bool Enabled, string Username, string Url, string? Password);

    public async Task<WebDavStatus> GetWebDavStatusAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<WebDavStatus>(await MeHrefAsync("webdavPassword", cancellationToken), cancellationToken) ?? new WebDavStatus(false, "", "", null);

    // Generate/regenerate — returns the plaintext password (shown once).
    public async Task<WebDavStatus> GenerateWebDavPasswordAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await MeHrefAsync("webdavPassword", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebDavStatus>(cancellationToken))!;
    }

    public async Task RevokeWebDavPasswordAsync(CancellationToken cancellationToken = default) =>
        (await _http.DeleteAsync(await MeHrefAsync("webdavPassword", cancellationToken), cancellationToken)).EnsureSuccessStatusCode();

    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") ----------------------------

    public sealed record MfaEnrollInfo(string Secret, string OtpauthUri, string QrDataUrl);

    // Starts enrollment: returns the secret + otpauth URI + QR data URL (the secret is stored server-side as
    // a pending, not-yet-active enrollment).
    public async Task<MfaEnrollInfo> EnrollMfaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await MeHrefAsync("mfaEnroll", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new MfaEnrollInfo(
            json.GetProperty("secret").GetString() ?? "",
            json.GetProperty("otpauthUri").GetString() ?? "",
            json.GetProperty("qrDataUrl").GetString() ?? "");
    }

    // Confirms enrollment with a code; returns the one-time recovery codes (shown once).
    public async Task<List<string>> EnableMfaAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await MeHrefAsync("mfaEnable", cancellationToken), new { code }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("That authentication code isn't right.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
    }

    public async Task DisableMfaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(await MeHrefAsync("mfa", cancellationToken), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Passkeys (ADR "Desktop passkey management") ------------------------------------------------
    // List + remove are plain API calls the native app makes directly; registration needs a browser
    // attestation ceremony and is delegated to the system browser (see OidcLoopbackAuthenticator).

    // RemoveHref is the row's own `self` rel: a passkey addresses itself, so removing one follows a link the
    // list already carried instead of rebuilding /users/me/passkeys/{id} from an id (issue #416).
    public sealed record PasskeyInfo(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, string? RemoveHref = null);

    public async Task<List<PasskeyInfo>> GetPasskeysAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("passkeys", cancellationToken), cancellationToken);
        var list = new List<PasskeyInfo>();
        if (json.TryGetProperty("passkeys", out var passkeys))
        {
            foreach (var p in passkeys.EnumerateArray())
            {
                var links = ParseLinks(p);
                list.Add(new PasskeyInfo(
                    p.GetProperty("id").GetGuid(),
                    p.GetProperty("name").GetString() ?? "",
                    p.GetProperty("createdAt").GetDateTimeOffset(),
                    p.TryGetProperty("lastUsedAt", out var lu) && lu.ValueKind != JsonValueKind.Null ? lu.GetDateTimeOffset() : null,
                    links is not null && links.TryGetValue("self", out var removeHref) ? removeHref : null));
            }
        }

        return list;
    }

    /// <summary>Removes a passkey by the address its own row advertised (its `self` rel).</summary>
    public async Task RemovePasskeyAsync(string removeHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(removeHref, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Notification email preferences (ADR "Notification preferences") -----------------------------

    public sealed record NotificationPreferenceInfo(int Type, string TypeName, bool EmailEnabled);

    public async Task<List<NotificationPreferenceInfo>> GetNotificationPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("notificationPreferences", cancellationToken), cancellationToken);
        var list = new List<NotificationPreferenceInfo>();
        if (json.TryGetProperty("preferences", out var prefs))
        {
            foreach (var p in prefs.EnumerateArray())
            {
                list.Add(new NotificationPreferenceInfo(
                    p.GetProperty("type").GetInt32(),
                    p.GetProperty("typeName").GetString() ?? "",
                    p.GetProperty("emailEnabled").GetBoolean()));
            }
        }

        return list;
    }

    public async Task SetNotificationPreferencesAsync(IEnumerable<NotificationPreferenceInfo> preferences, CancellationToken cancellationToken = default)
    {
        var body = new { preferences = preferences.Select(p => new { type = p.Type, emailEnabled = p.EmailEnabled }) };
        using var response = await _http.PutAsJsonAsync(await MeHrefAsync("notificationPreferences", cancellationToken), body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Legal holds (ADR "Legal hold & retention enforcement") -------------------------------------

    // A hold, carrying the addresses its own row advertised (ADR 0543/0555): `self`, plus `release`/`add-item`
    // only while it is active — a released hold offers neither, so the affordance is the server's answer.
    public sealed record LegalHoldInfo(Guid Id, string Name, string? Reason, DateTimeOffset PlacedAt, bool IsActive, int ItemCount, List<LegalHoldItemInfo> Items, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // A covered document. RemoveHref is the pairing's own address — the item is the only thing that knows both
    // ends of it — and is null once the hold is released.
    public sealed record LegalHoldItemInfo(Guid DocumentId, string DocumentName, string? RemoveHref = null, Guid? ParentId = null);

    public async Task<List<LegalHoldInfo>> GetLegalHoldsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("legalHolds", cancellationToken), cancellationToken);
        var list = new List<LegalHoldInfo>();
        if (json.TryGetProperty("holds", out var holds))
        {
            foreach (var h in holds.EnumerateArray())
            {
                list.Add(ParseLegalHold(h));
            }
        }

        return list;
    }

    public async Task<LegalHoldInfo> GetLegalHoldAsync(LegalHoldInfo hold, CancellationToken cancellationToken = default) =>
        ParseLegalHold(await _http.GetFromJsonAsync<JsonElement>(RequireHref(hold, "self"), cancellationToken));

    public async Task<LegalHoldInfo> CreateLegalHoldAsync(string name, string? reason, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("legalHolds", cancellationToken), new { name, reason }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to place legal holds.");
        }

        response.EnsureSuccessStatusCode();
        return ParseLegalHold(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task AddLegalHoldItemAsync(LegalHoldInfo hold, Guid documentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(hold, "add-item"), new { documentId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("The document is already on this hold.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveLegalHoldItemAsync(LegalHoldItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(
            item.RemoveHref ?? throw new InvalidOperationException($"'{item.DocumentName}' advertised no 'remove' rel — a released hold offers none (ADR 0543/0555)."),
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task ReleaseLegalHoldAsync(LegalHoldInfo hold, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(hold, "release"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static LegalHoldInfo ParseLegalHold(JsonElement e)
    {
        var items = new List<LegalHoldItemInfo>();
        if (e.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in itemsEl.EnumerateArray())
            {
                items.Add(new LegalHoldItemInfo(i.GetProperty("documentId").GetGuid(), i.GetProperty("documentName").GetString() ?? "", ApiCore.RelHref(i, "remove"), i.TryGetProperty("parentId", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetGuid() : null));
            }
        }

        return new LegalHoldInfo(
            e.GetProperty("id").GetGuid(),
            e.GetProperty("name").GetString() ?? "",
            e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            e.GetProperty("placedAt").GetDateTimeOffset(),
            e.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True,
            e.TryGetProperty("itemCount", out var c) ? c.GetInt32() : items.Count,
            items,
            ParseLinks(e));
    }

    // ---- Retention schedule (ADR "Retention policies (auto-disposition)") ---------------------------

    // A scheduled document. `dispose` is CONDITIONAL server-side — absent while a review is required or a hold
    // suspends it — so the row's own links are what decide whether the action is offered (ADR 0543/0555).
    public sealed record RetentionItemInfo(Guid DocumentId, string DocumentName, int RetentionYears, string DispositionDate, bool Overdue, bool SuspendedByHold, string? RetentionOverrideUntil, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }
    public sealed record RetentionScheduleInfo(IReadOnlyList<RetentionItemInfo> Items, bool RequiresReview);

    public async Task<RetentionScheduleInfo> GetRetentionScheduleAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("retentionSchedule", cancellationToken), cancellationToken);
        var list = new List<RetentionItemInfo>();
        if (json.TryGetProperty("items", out var items))
        {
            foreach (var i in items.EnumerateArray())
            {
                list.Add(new RetentionItemInfo(
                    i.GetProperty("documentId").GetGuid(),
                    i.GetProperty("documentName").GetString() ?? "",
                    i.TryGetProperty("retentionYears", out var y) ? y.GetInt32() : 0,
                    i.GetProperty("dispositionDate").GetString() ?? "",
                    i.TryGetProperty("overdue", out var o) && o.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("suspendedByHold", out var s) && s.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("retentionOverrideUntil", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null,
                    ParseLinks(i)));
            }
        }

        return new RetentionScheduleInfo(list, json.TryGetProperty("requiresReview", out var rr) && rr.ValueKind == JsonValueKind.True);
    }

    // Manually dispose an eligible document (ADR "Retention review-before-disposition").
    public async Task DisposeRetentionAsync(RetentionItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(item, "dispose"), null, cancellationToken);
        await ThrowIfProblemAsync(response, "Could not dispose the document.", cancellationToken);
    }

    // Extend a document's retention to a new "retain until" date ("yyyy-MM-dd").
    public async Task ExtendRetentionAsync(RetentionItemInfo item, string until, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(item, "extend"), new { until }, cancellationToken);
        await ThrowIfProblemAsync(response, "Could not extend retention.", cancellationToken);
    }

    // Writes the rights at the address the ROW gave us for writing them — `grant` on a principal being added,
    // `edit` on an entry already there. One method, because it is one operation: the two rels differ only in
    // which side of the same address the server chose to advertise (ADR 0555).
    public async Task SetAclEntryAsync(IAdvertisesLinks row, AclRights rights, CancellationToken cancellationToken = default)
    {
        var href = row.Href("grant") ?? row.Href("edit")
            ?? throw new InvalidOperationException($"The row '{row.Name}' advertised neither 'grant' nor 'edit' — you may not change its access (ADR 0543/0555).");
        using var response = await _http.PutAsJsonAsync(href, rights, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    internal static UserOptionInfo ParseMember(JsonElement e) =>
        new(e.GetProperty("id").GetGuid(),
            e.GetProperty("displayName").GetString() ?? "",
            ParseLinks(e) is { } links && links.TryGetValue("remove", out var removeHref) ? removeHref : null);

    public async Task SetMyPhotoAsync(byte[] png, CancellationToken cancellationToken = default) =>
        await Core.PutPhotoAsync(await MeHrefAsync("photo", cancellationToken), png, cancellationToken);

    /// <summary>The caller's OWN avatar, at the address the `me` resource advertises for it.</summary>
    public async Task<byte[]?> GetMyPhotoAsync(CancellationToken cancellationToken = default) =>
        await Core.GetPhotoAsync(await MeHrefAsync("photo", cancellationToken), cancellationToken);


    /// <summary>Removes the caller's OWN avatar, at the address the `me` resource advertises.</summary>
    public Task DeleteMyPhotoAsync(CancellationToken cancellationToken = default) =>
        Core.DeletePhotoAsync(MeHrefAsync("photo", cancellationToken), cancellationToken);


    internal static string? StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;


    private static string? FindLink(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links))
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.GetProperty("rel").GetString() == rel)
            {
                return link.GetProperty("href").GetString();
            }
        }

        return null;
    }
}
