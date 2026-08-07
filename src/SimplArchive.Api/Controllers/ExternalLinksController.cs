using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
    /// Redeem a token. <c>?download=true</c> forces a save-as; otherwise the file opens inline.
    /// </summary>
    // No companion HEAD, against the standing every-GET-has-a-HEAD convention. A HEAD here would be a CHEAPER
    // oracle for probing which tokens exist than the GET is — existence for free, with no body. Omitted on
    // purpose (ADR 0546).
    [HttpGet("{token}")]
    public async Task<IActionResult> Redeem(string token, [FromQuery] bool download, CancellationToken cancellationToken)
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
            return Gone();
        }

        _tenant.TenantId = link.TenantId;

        var tenant = await _dbContext.Tenants
            .IgnoreQueryFilters(["TenantFilter"])
            .FirstOrDefaultAsync(t => t.Id == link.TenantId, cancellationToken);

        // The tenant switch is checked HERE, not only at creation: turning it off is meant to stop links already
        // in the wild, which is what an administrator reaching for it during a leak actually wants (ADR 0546).
        if (tenant is not { AllowExternalLinks: true } || !link.IsLive(now))
        {
            return Gone();
        }

        // The document is resolved through the ordinary (tenant-filtered, soft-delete-filtered) set, so a deleted
        // document stops serving without any extra check.
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == link.DocumentId, cancellationToken);
        if (document is null)
        {
            return Gone();
        }

        // Legal holds and retention deliberately do NOT block this: a hold freezes mutation, not reading, and an
        // authenticated user can already download a held document. See ADR 0546 — a link made before a hold keeps
        // working after it, and the remedy is to revoke the link or use the tenant kill switch.
        var version = await CurrentVersion.ResolveAsync(
            _dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);

        if (version?.ObjectKey is not { Length: > 0 } objectKey)
        {
            return Gone();
        }

        link.AccessCount++;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var fileName = document.Name + Path.GetExtension(objectKey);
        var url = await _objectStorage.GetPresignedDownloadUrlAsync(
            objectKey, PresignedUrlExpiry, download ? fileName : null, cancellationToken);

        // Recorded against the LINK, not a principal — there isn't one. RecordForActorAsync exists for exactly
        // this shape: a path with no current principal that still knows who acted. The token itself never
        // appears; the link id identifies the row without putting a live credential into a log that is exported
        // and streamed to a SIEM (ADR 0546).
        await _audit.RecordForActorAsync(
            AuditActorType.ExternalLink, link.Id, "External link", link.TenantId,
            AuditActions.ExternalLinkAccessed, "Document", document.Id, document.Name,
            $"External link {link.Id} accessed ({link.AccessCount} of {link.MaxAccesses?.ToString() ?? "unlimited"})",
            cancellationToken);

        return Ok(new ExternalLinkRedemptionResource
        {
            DownloadUrl = url.ToString(),
            FileName = fileName,
            ExpiresAt = link.ExpiresAt,
        });
    }

    // ONE response for every rejection: unknown token, expired, exhausted, revoked, tenant switched off, document
    // gone. Distinguishing them would turn this endpoint into an oracle for which tokens exist, which is the
    // whole point of not distinguishing them (ADR 0546).
    //
    // 410 Gone rather than 404: it says "this link is no longer usable" without implying anything about whether it
    // ever existed, and it is the same answer in every case.
    private IActionResult Gone() =>
        StatusCode(StatusCodes.Status410Gone, new
        {
            title = "Gone",
            status = StatusCodes.Status410Gone,
            detail = "This link is no longer available.",
            errorCode = "EXTERNAL_LINK_UNAVAILABLE",
        });
}
