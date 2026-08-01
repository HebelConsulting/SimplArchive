using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Duplicate detection by content hash (ADR "Duplicate document detection"). Given a SHA-256, returns the
/// tenant's documents whose **latest confirmed version** is byte-identical (same hash) — ACL-filtered to what
/// the caller can see, so a duplicate the caller can't access is never revealed. Backs the upload-time
/// "this file already exists" modal (reference it / file anyway / cancel); the client computes the hash of the
/// file it's about to upload and asks here before creating the document.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/duplicates")]
[Authorize]
public class DuplicatesController : ControllerBase
{
    private const int MaxResults = 50;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRights;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public DuplicatesController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRights,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _effectiveRights = effectiveRights;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
    }

    public class DuplicatesResource : HypermediaResource
    {
        public List<DuplicateResource> Duplicates { get; set; } = [];
    }

    public class DuplicateResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    [HttpGet]
    public async Task<IActionResult> Find([FromQuery] string? hash, CancellationToken cancellationToken)
    {
        var normalized = hash?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return Ok(new DuplicatesResource { Links = SelfLinks(hash) });
        }

        // Documents that have ANY confirmed version with this hash (candidates); then keep only those whose
        // *latest* confirmed version matches (current-content duplicates, ADR decision).
        var candidateIds = await _dbContext.DocumentVersions
            .Where(v => v.Status == DocumentVersionStatus.Confirmed && v.Sha256Hash == normalized)
            .Select(v => v.DocumentId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var results = new List<DuplicateResource>();
        foreach (var docId in candidateIds)
        {
            // The current-content hash honoring the CurrentVersionId pointer (issue #265), else the latest confirmed.
            var pointer = await _dbContext.Documents.Where(d => d.Id == docId).Select(d => d.CurrentVersionId).FirstOrDefaultAsync(cancellationToken);
            var currentVersion = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, docId, pointer, cancellationToken);

            if (currentVersion?.Sha256Hash != normalized)
            {
                continue; // an older version matched, but the current content differs — not a current duplicate
            }

            if (!await CanSeeAsync(docId, cancellationToken))
            {
                continue; // never reveal a document the caller can't access
            }

            var doc = await _dbContext.Documents
                .Where(d => d.Id == docId)
                .Select(d => new { d.Name, d.ParentId })
                .SingleOrDefaultAsync(cancellationToken);
            if (doc is null)
            {
                continue;
            }

            results.Add(new DuplicateResource
            {
                Id = docId,
                Name = doc.Name,
                Path = await BuildPathAsync(doc.ParentId, cancellationToken),
                Links = [new Link("open", $"/api/documents/{docId}", "GET")],
            });

            if (results.Count >= MaxResults)
            {
                break;
            }
        }

        return Ok(new DuplicatesResource { Duplicates = results, Links = SelfLinks(hash) });
    }

    // Standing convention: every GET action gets a companion HEAD.
    [HttpHead]
    public IActionResult Head() => NoContent();

    private static List<Link> SelfLinks(string? hash) => [new("self", $"/api/duplicates?hash={hash}", "GET")];

    private async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (await _effectiveRights.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken)).CanSee;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _effectiveRights.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee;
        }

        return false;
    }

    // "Repositories / Folder / Subfolder" — the containing-folder path, walking up ParentId.
    private async Task<string> BuildPathAsync(Guid? parentId, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var current = parentId;
        while (current is { } id)
        {
            var node = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Name, d.ParentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (node is null)
            {
                break;
            }

            segments.Insert(0, node.Name);
            current = node.ParentId;
        }

        return string.Join(" / ", segments);
    }
}
