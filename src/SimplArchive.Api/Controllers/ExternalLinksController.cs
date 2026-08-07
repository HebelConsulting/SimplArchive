using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The one ANONYMOUS content endpoint in the system (ADR 0546, issue #385): redeems an external link's token for
/// a short-lived presigned URL to the shared document's current version.
///
/// Everything else here sits behind OpenIddict. Here the token in the URL <em>is</em> the credential, so this
/// controller is written against a different threat model: token guessing, probing for which tokens exist,
/// denial of service, and an audit trail with no principal to attribute anything to.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/external-links")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.ExternalLinks)]
public class ExternalLinksController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;
    // The CONCRETE accessor, not the interface: the interface exposes TenantId read-only, and this path has
    // to SET it from the resolved link — the same thing WebDavMiddleware does for its own pre-tenant lookup.
    private readonly CurrentTenantAccessor _tenant;
    private readonly IAuditRecorder _audit;
    private readonly TimeProvider _clock;

    public ExternalLinksController(
        SimplArchiveDbContext dbContext,
        IObjectStorageClient objectStorage,
        CurrentTenantAccessor tenant,
        IAuditRecorder audit,
        TimeProvider clock)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
    }

    // Deliberately shorter than any link's own expiry, so a URL captured from a response cannot outlive the share
    // it came from. Long enough for a browser to start the transfer, not long enough to pass around.
    private static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(2);

    public class ExternalLinkRedemptionResource : HypermediaResource
    {
        public string DownloadUrl { get; set; } = "";

        public string FileName { get; set; } = "";

        public DateTimeOffset ExpiresAt { get; set; }
    }

    /// <summary>
    /// Redeem a token: an HTML page for a browser, the JSON resource for a programmatic caller.
    /// </summary>
    /// <remarks>
    /// This URL is what gets pasted into a mail, a chat or a browser bar, so its audience is overwhelmingly a
    /// PERSON with no account and no client — who used to be shown a JSON document with a URL buried in it.
    /// Negotiated on <c>Accept</c> rather than split into two URLs, because one link that works everywhere is the
    /// entire product here; anything asking for JSON explicitly still gets it.
    /// </remarks>
    // No companion HEAD, against the standing every-GET-has-a-HEAD convention. A HEAD here would be a CHEAPER
    // oracle for probing which tokens exist than the GET is — existence for free, with no body. Omitted on
    // purpose (ADR 0546).
    [HttpGet("{token}")]
    public async Task<IActionResult> Redeem(string token, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(token, cancellationToken) is not ({ } link, { } document, { } objectKey))
        {
            return Gone();
        }

        // The redemption is counted HERE, once, whichever representation was asked for: opening the link is what
        // "using the share" means. Taking delivery afterwards — the Open or Download button, i.e. the content
        // route below — is the same redemption continuing, not a second one, so it does not count again.
        link.AccessCount++;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var fileName = document.Name + Path.GetExtension(objectKey);

        // Recorded against the LINK, not a principal — there isn't one. RecordForActorAsync exists for exactly
        // this shape: a path with no current principal that still knows who acted. The token itself never
        // appears; the link id identifies the row without putting a live credential into a log that is exported
        // and streamed to a SIEM (ADR 0546).
        await _audit.RecordForActorAsync(
            AuditActorType.ExternalLink, link.Id, "External link", link.TenantId,
            AuditActions.ExternalLinkAccessed, "Document", document.Id, document.Name,
            $"External link {link.Id} accessed ({link.AccessCount} of {link.MaxAccesses?.ToString() ?? "unlimited"})",
            cancellationToken);

        if (WantsHtml())
        {
            return Html(StatusCodes.Status200OK,
                ExternalLinkPage.Live(fileName, link.ExpiresAt, $"/api/external-links/{token}/content"));
        }

        var url = await _objectStorage.GetPresignedDownloadUrlAsync(objectKey, PresignedUrlExpiry, null, cancellationToken);
        return Ok(new ExternalLinkRedemptionResource
        {
            DownloadUrl = url.ToString(),
            FileName = fileName,
            ExpiresAt = link.ExpiresAt,
        });
    }

    /// <summary>
    /// Hands over the bytes: mints a presigned URL and redirects to it. <c>?download=true</c> forces a save-as.
    /// </summary>
    /// <remarks>
    /// A separate route so the landing page carries no storage URL of its own. A presigned URL lives two minutes;
    /// one embedded in the page would be dead by the time a person had read it and decided, and they would meet
    /// the storage provider's raw XML error instead of anything of ours. Minting at click time means the page can
    /// sit open for as long as the reader likes.
    ///
    /// Every liveness check runs again here — the link can expire, be revoked or be switched off between the page
    /// loading and the button being pressed — but the access count is NOT incremented: this is the delivery half
    /// of a redemption already counted, and counting it twice would halve every cap an administrator set.
    /// </remarks>
    [HttpGet("{token}/content")]
    public async Task<IActionResult> Content(string token, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(token, cancellationToken) is not ({ } _, { } document, { } objectKey))
        {
            return Gone();
        }

        var fileName = document.Name + Path.GetExtension(objectKey);

        // Two different presigns, because the disposition is the whole difference between the two buttons:
        // "attachment" makes the browser save the file, "inline" lets it render one it understands. The download
        // presign hardcodes attachment, so asking it for an inline view by passing no file name — which is what
        // the old ?download=true switch did — produced a save-as either way. "Open document" has to be the
        // PREVIEW presign to actually open anything.
        //
        // And "inline" alone is not enough: objects are stored as application/octet-stream, and NO browser renders
        // a PDF served as that — it downloads it, disposition notwithstanding. So the content type is overridden
        // on the presigned URL, derived from the extension. Without this the Open button silently behaved as a
        // second Download button, which is exactly what it looked like in the browser.
        var url = download
            ? await _objectStorage.GetPresignedDownloadUrlAsync(objectKey, PresignedUrlExpiry, fileName, cancellationToken)
            : await _objectStorage.GetPresignedPreviewUrlAsync(
                objectKey, PresignedUrlExpiry, fileName,
                WebDav.ContentTypes.ForExtension(Path.GetExtension(objectKey)), cancellationToken);

        return Redirect(url.ToString());
    }

    // Everything a redemption has to be true for, in one place, so the landing page and the content route cannot
    // drift apart on what "usable" means. Returns nulls rather than throwing: every failure is the same answer.
    private async Task<(ExternalLink? Link, Document? Document, string? ObjectKey)> ResolveAsync(
        string token, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        // Resolving the link runs BEFORE the tenant is known — the token is what identifies the tenant — so the
        // lookup ignores the tenant filter, exactly as login and client_id resolution do. The tenant accessor is
        // set from the row before anything else touches the database.
        var link = await _dbContext.ExternalLinks
            .IgnoreQueryFilters(["TenantFilter"])
            .FirstOrDefaultAsync(l => l.Token == token, cancellationToken);

        if (link is null)
        {
            return (null, null, null);
        }

        _tenant.TenantId = link.TenantId;

        var tenant = await _dbContext.Tenants
            .IgnoreQueryFilters(["TenantFilter"])
            .FirstOrDefaultAsync(t => t.Id == link.TenantId, cancellationToken);

        // The tenant switch is checked HERE, not only at creation: turning it off is meant to stop links already
        // in the wild, which is what an administrator reaching for it during a leak actually wants (ADR 0546).
        if (tenant is not { AllowExternalLinks: true } || !link.IsLive(now))
        {
            return (null, null, null);
        }

        // The document is resolved through the ordinary (tenant-filtered, soft-delete-filtered) set, so a deleted
        // document stops serving without any extra check.
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == link.DocumentId, cancellationToken);
        if (document is null)
        {
            return (null, null, null);
        }

        // Legal holds and retention deliberately do NOT block this: a hold freezes mutation, not reading, and an
        // authenticated user can already download a held document. See ADR 0546 — a link made before a hold keeps
        // working after it, and the remedy is to revoke the link or use the tenant kill switch.
        var version = await CurrentVersion.ResolveAsync(
            _dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);

        return version?.ObjectKey is { Length: > 0 } objectKey ? (link, document, objectKey) : (null, null, null);
    }

    // A browser asks for text/html; a programmatic caller asks for JSON, or asks for nothing in particular from a
    // tool that will not render markup. Treating "*/*" as HTML would be wrong for curl and right for nothing, so
    // only an explicit text/html preference switches representation.
    private bool WantsHtml() =>
        Request.GetTypedHeaders().Accept?.Any(h => h.MediaType.HasValue
            && h.MediaType.Value!.Equals("text/html", StringComparison.OrdinalIgnoreCase)) == true;

    // ONE response for every rejection: unknown token, expired, exhausted, revoked, tenant switched off, document
    // gone. Distinguishing them would turn this endpoint into an oracle for which tokens exist, which is the
    // whole point of not distinguishing them (ADR 0546).
    //
    // 410 Gone rather than 404: it says "this link is no longer usable" without implying anything about whether it
    // ever existed, and it is the same answer in every case. A browser gets that sentence as a page it can read,
    // rather than a problem document — the recipient of a dead link is the least equipped person in the system to
    // interpret JSON.
    private IActionResult Gone() =>
        WantsHtml()
            ? Html(StatusCodes.Status410Gone, ExternalLinkPage.Gone())
            : StatusCode(StatusCodes.Status410Gone, new
            {
                title = "Gone",
                status = StatusCodes.Status410Gone,
                detail = "This link is no longer available.",
                errorCode = "EXTERNAL_LINK_UNAVAILABLE",
            });

    private ContentResult Html(int statusCode, string html) =>
        new() { StatusCode = statusCode, ContentType = "text/html; charset=utf-8", Content = html };
}
