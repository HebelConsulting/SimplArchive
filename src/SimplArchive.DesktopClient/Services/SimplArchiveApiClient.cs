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
    // internal: IntrayApi PUTs bytes to a presigned URL too, since the intray calls moved there (#443).
    internal static HttpClient Anonymous => ApiCore.Anonymous;
    private readonly HttpClient _http;
    private IntrayApi? _intray;
    private CheckoutClient? _checkout;
    private SearchClient? _search;
    private AdminClient? _admin;
    private DocumentsClient? _documents;
    private WorkflowClient? _workflow;
    private NotificationsClient? _notifications;
    private RemindersClient? _reminders;
    private AnnotationsClient? _annotations;
    private AuditClient? _audit;
    private VersionsClient? _versions;
    private RecycleBinClient? _recycleBin;
    private ExternalLinksClient? _externalLinks;
    private ProfileClient? _profile;
    private DavCollectionsClient? _davCollections;
    private LegalHoldsClient? _legalHolds;
    private StructuredEditorClient? _structuredEditors;

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

    /// <summary>The audit-log area (#443, ops tranche).</summary>
    public AuditClient Audit => _audit ??= new AuditClient(Core);

    /// <summary>The versions area (#443, ops tranche).</summary>
    public VersionsClient Versions => _versions ??= new VersionsClient(Core);

    /// <summary>The recycle-bin area (#443, ops tranche).</summary>
    public RecycleBinClient RecycleBin => _recycleBin ??= new RecycleBinClient(Core);

    /// <summary>The external-sharing-links area (#443, ops tranche).</summary>
    public ExternalLinksClient ExternalLinks => _externalLinks ??= new ExternalLinksClient(Core);

    /// <summary>The signed-in account's own area (#443, ops tranche).</summary>
    public ProfileClient Profile => _profile ??= new ProfileClient(Core);

    /// <summary>The caller's addressbooks and calendars, for the Contacts/Calendar tabs (#564).</summary>
    public DavCollectionsClient DavCollections => _davCollections ??= new DavCollectionsClient(Core, Profile);

    /// <summary>The legal-holds + retention area (#443, ops tranche).</summary>
    public LegalHoldsClient LegalHolds => _legalHolds ??= new LegalHoldsClient(Core);

    /// <summary>The structured contact/appointment editors' read + save plumbing (#564, ADR 0631).</summary>
    public StructuredEditorClient StructuredEditors =>
        _structuredEditors ??= new StructuredEditorClient(Core, Documents);

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


    // The signed-in principal's ids + display names (ADR "S3-backed inbox") — names drive the local folder
    // path. IsTenantAdmin gates admin-only actions (e.g. the searchable-PDF backfill).
    public sealed record WhoAmIInfo(Guid? UserId, Guid? TenantId, string? TenantName, string? UserName, bool IsTenantAdmin, bool CanManageUsers, bool HasPhoto, bool CanViewAuditLog, bool MfaEnabled, bool CanResetMfa, bool CanLegalHold, bool CanManageClassification, bool CanOverrideCheckout = false, bool CanImpersonate = false, string? ImpersonatedBy = null, bool CanExport = false, bool CanImport = false, bool CanManageIntrayes = false, bool CanManageServiceAccounts = false);


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
            json.TryGetProperty("canManageIntrayes", out var cmi) && cmi.ValueKind == JsonValueKind.True,
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

    /// <summary>The intray's own api surface (ADR 0575) — its listing and its page operations.</summary>
    public IntrayApi Intray => _intray ??= new IntrayApi(Core);

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


    internal static DateTimeOffset? OptDate(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;


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


    internal static UserOptionInfo ParseMember(JsonElement e) =>
        new(e.GetProperty("id").GetGuid(),
            e.GetProperty("displayName").GetString() ?? "",
            ParseLinks(e) is { } links && links.TryGetValue("remove", out var removeHref) ? removeHref : null);


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
