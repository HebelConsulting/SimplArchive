using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Api.Errors.Exceptions.ExternalLinks;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Managing a document's external links (ADR 0546, issue #385) — create, list, extend, revoke. The anonymous
/// redemption endpoint lives in <see cref="ExternalLinksController"/>; everything here is authenticated and
/// gated on <c>CanCreateExternalLink</c> plus the tenant's <c>AllowExternalLinks</c> switch.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/external-links")]
[Authorize]
public class DocumentExternalLinksController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _rights;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccount;
    private readonly ICurrentTenantAccessor _tenant;
    private readonly IAuditRecorder _audit;
    private readonly TimeProvider _clock;

    public DocumentExternalLinksController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator rights,
        IUserSystemRightsResolver userSystemRights,
        ICurrentUserAccessor currentUser,
        ICurrentServiceAccountAccessor currentServiceAccount,
        ICurrentTenantAccessor tenant,
        IAuditRecorder audit,
        TimeProvider clock)
    {
        _dbContext = dbContext;
        _rights = rights;
        _userSystemRights = userSystemRights;
        _currentUser = currentUser;
        _currentServiceAccount = currentServiceAccount;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
    }

    // A link may only be extended when it is close to running out, so "extend" stays a renewal rather than a way
    // to keep pushing a share indefinitely into the future.
    private const int ExtendableWithinDays = 30;
    private const int MaxExtensionDays = 90;

    public class ExternalLinkResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public Guid DocumentId { get; set; }

        public string DocumentName { get; set; } = "";

        // The full redemption URL, returned ONLY when the link is created. Listing never returns it: the token is
        // a live credential, and a list endpoint is a far broader surface than a single create response (ADR 0546).
        public string? Url { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public int? MaxAccesses { get; set; }

        public int AccessCount { get; set; }

        public string CreatedByName { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }

        // Client hint: whether the extend control should be offered (expiry within 30 days).
        public bool CanExtend { get; set; }

        public string Etag { get; set; } = "";
    }

    public class ExternalLinkListResource : HypermediaResource
    {
        public List<ExternalLinkResource> ExternalLinks { get; set; } = [];

        public bool CanCreate { get; set; }
    }

    public class CreateExternalLinkRequest
    {
        public DateTimeOffset? ExpiresAt { get; set; }

        public int? MaxAccesses { get; set; }
    }

    public class ExtendExternalLinkRequest
    {
        public int Days { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!(await GetRightsAsync(documentId, cancellationToken)).CanReadContent)
        {
            return Forbid();
        }

        var now = _clock.GetUtcNow();

        // Live links only — expired and revoked rows are retained as evidence but are not the working list.
        var links = await _dbContext.ExternalLinks
            .Where(l => l.DocumentId == documentId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.ExpiresAt).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.DocumentId,
                l.ExpiresAt,
                l.MaxAccesses,
                l.AccessCount,
                l.CreatedAt,
                l.ConcurrencyToken,
                CreatedByName = l.CreatedByUserId != null
                    ? _dbContext.Users.Where(u => u.Id == l.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault()
                    : _dbContext.ServiceAccounts.Where(s => s.Id == l.CreatedByServiceAccountId).Select(s => s.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return Ok(new ExternalLinkListResource
        {
            ExternalLinks = links.Select(l => new ExternalLinkResource
            {
                Id = l.Id,
                DocumentId = l.DocumentId,
                ExpiresAt = l.ExpiresAt,
                MaxAccesses = l.MaxAccesses,
                AccessCount = l.AccessCount,
                CreatedByName = l.CreatedByName ?? "Unknown",
                CreatedAt = l.CreatedAt,
                CanExtend = l.ExpiresAt <= now.AddDays(ExtendableWithinDays),
                Etag = l.ConcurrencyToken.ToString(),
                Links = [new Link("revoke", $"/api/documents/{documentId}/external-links/{l.Id}", "DELETE")],
            }).ToList(),
            CanCreate = await CanCreateAsync(documentId, cancellationToken),
            Links = [new Link("self", $"/api/documents/{documentId}/external-links", "GET")],
        });
    }

    // Standing convention: every GET action gets a companion HEAD.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return (await GetRightsAsync(documentId, cancellationToken)).CanReadContent ? NoContent() : Forbid();
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid documentId, [FromBody] CreateExternalLinkRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var tenant = await CurrentTenantAsync(cancellationToken);
        if (!tenant.AllowExternalLinks)
        {
            throw new ExternalLinksDisabledException();
        }

        if (!await CanCreateAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // A folder has no version to serve.
        if (!await _dbContext.DocumentVersions.AnyAsync(v => v.DocumentId == documentId, cancellationToken))
        {
            throw new CannotShareFolderException();
        }

        var now = _clock.GetUtcNow();
        var expiresAt = request.ExpiresAt ?? now.AddDays(tenant.ExternalLinkMaxDays);
        if (expiresAt <= now || expiresAt > now.AddDays(tenant.ExternalLinkMaxDays))
        {
            throw new InvalidExternalLinkExpiryException(tenant.ExternalLinkMaxDays);
        }

        var (userId, serviceAccountId) = GetCallerIdentity();
        var link = new ExternalLink
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId!.Value,
            DocumentId = documentId,
            Token = ExternalLinkToken.Create(),
            ExpiresAt = expiresAt,
            MaxAccesses = request.MaxAccesses ?? tenant.ExternalLinkDefaultAccesses,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = now,
        };

        _dbContext.ExternalLinks.Add(link);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The link id, never the token — an audit log is exported and streamed to a SIEM (ADR 0546).
        await _audit.RecordAsync(AuditActions.ExternalLinkCreated, "Document", documentId, document.Name,
            $"External link {link.Id} created, expires {expiresAt:u}", cancellationToken: cancellationToken);

        SetETag(link.ConcurrencyToken);
        return StatusCode(StatusCodes.Status201Created, new ExternalLinkResource
        {
            Id = link.Id,
            DocumentId = documentId,
            DocumentName = document.Name,
            // The ONLY response that carries the token. After this the sharer must keep the URL; the system will
            // not hand it out again, so a leaked list cannot become a leaked set of live credentials.
            Url = $"{Request.Scheme}://{Request.Host}/api/external-links/{link.Token}",
            ExpiresAt = link.ExpiresAt,
            MaxAccesses = link.MaxAccesses,
            AccessCount = 0,
            CreatedAt = link.CreatedAt,
            CanExtend = link.ExpiresAt <= now.AddDays(ExtendableWithinDays),
            Etag = link.ConcurrencyToken.ToString(),
        });
    }

    // Extend measured FROM TODAY, not from the current expiry: a link with 29 days left extended by 90 lands 90
    // days out, not 119. Predictable, and it cannot be chained past what the creation cap implies (ADR 0546).
    [HttpPut("{linkId:guid}/expiry")]
    public async Task<IActionResult> Extend(Guid documentId, Guid linkId, [FromBody] ExtendExternalLinkRequest request, CancellationToken cancellationToken)
    {
        var link = await _dbContext.ExternalLinks
            .SingleOrDefaultAsync(l => l.Id == linkId && l.DocumentId == documentId, cancellationToken);
        if (link is null)
        {
            return NotFound();
        }

        var tenant = await CurrentTenantAsync(cancellationToken);
        if (!tenant.AllowExternalLinks)
        {
            throw new ExternalLinksDisabledException();
        }

        if (!await CanCreateAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var now = _clock.GetUtcNow();
        if (request.Days is < 1 || request.Days > MaxExtensionDays)
        {
            throw new InvalidExternalLinkExpiryException(tenant.ExternalLinkMaxDays);
        }

        var ifMatch = RequireIfMatch();
        link.ExpiresAt = now.AddDays(request.Days);
        _dbContext.Entry(link).Property(l => l.ConcurrencyToken).OriginalValue = ifMatch;
        await SaveWithConcurrencyAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.ExternalLinkExtended, "Document", documentId, await DocumentNameAsync(documentId, cancellationToken),
            $"External link {link.Id} extended to {link.ExpiresAt:u}", cancellationToken: cancellationToken);

        SetETag(link.ConcurrencyToken);
        return NoContent();
    }

    // Revocation STAMPS the row rather than deleting it: the record of what was shared, and when, is exactly what
    // an investigation needs after a token leaks.
    [HttpDelete("{linkId:guid}")]
    public async Task<IActionResult> Revoke(Guid documentId, Guid linkId, CancellationToken cancellationToken)
    {
        var link = await _dbContext.ExternalLinks
            .SingleOrDefaultAsync(l => l.Id == linkId && l.DocumentId == documentId, cancellationToken);
        if (link is null)
        {
            return NotFound();
        }

        if (!await CanCreateAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // No tenant-switch check: revoking must stay possible even after the feature is switched off, or an
        // administrator who disables it can no longer tidy up the links it left behind.
        var ifMatch = RequireIfMatch();
        link.RevokedAt = _clock.GetUtcNow();
        _dbContext.Entry(link).Property(l => l.ConcurrencyToken).OriginalValue = ifMatch;
        await SaveWithConcurrencyAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.ExternalLinkRevoked, "Document", documentId, await DocumentNameAsync(documentId, cancellationToken),
            $"External link {link.Id} revoked", cancellationToken: cancellationToken);

        return NoContent();
    }

    // Creating a link needs BOTH the system right and CanReadContent on the document itself: the right says you
    // may share, the ACL says you may read this particular thing. Neither alone is enough.
    private async Task<bool> CanCreateAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (!(await GetRightsAsync(documentId, cancellationToken)).CanReadContent)
        {
            return false;
        }

        if (_currentServiceAccount.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanCreateExternalLink)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return _currentUser.UserId is { } userId
            && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanCreateExternalLink;
    }

    private async Task<EffectiveRights> GetRightsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccount.ServiceAccountId is { } serviceAccountId)
        {
            return await _rights.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken);
        }

        return _currentUser.UserId is { } userId
            ? await _rights.GetEffectiveRightsAsync(userId, documentId, cancellationToken)
            : NoRights;
    }

    // An unauthenticated caller can't reach these actions ([Authorize]), so this is a defensive floor rather
    // than a real branch.
    private static readonly EffectiveRights NoRights = new(false, false, false, false, false, false, false, false, false);

    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity() =>
        _currentServiceAccount.ServiceAccountId is { } sid ? (null, sid) : (_currentUser.UserId, null);

    private async Task<Domain.Tenants.Tenant> CurrentTenantAsync(CancellationToken cancellationToken) =>
        await _dbContext.Tenants.SingleAsync(t => t.Id == _tenant.TenantId!.Value, cancellationToken);

    private async Task<string> DocumentNameAsync(Guid documentId, CancellationToken cancellationToken) =>
        await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken) ?? "";

    private Guid RequireIfMatch()
    {
        if (!Request.Headers.TryGetValue("If-Match", out var values) || !Guid.TryParse(values.ToString().Trim('"'), out var token))
        {
            throw new IfMatchRequiredException();
        }

        return token;
    }

    private async Task SaveWithConcurrencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForExternalLink();
        }
    }

    private void SetETag(Guid concurrencyToken) => Response.Headers.ETag = $"\"{concurrencyToken}\"";
}
