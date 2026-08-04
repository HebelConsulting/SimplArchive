using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Search;

// Metadata-only search fallback (ADR "Metadata search (first slice)", 0249) — used when OpenSearch isn't
// configured. A case-insensitive query against existing Postgres data (Document.Name + FieldValue.Value +
// DocumentAnnotation.Text, ADR 0526);
// nothing new is stored. Ordered by Name (a stable, provider-agnostic order — no relevance, unlike the
// OpenSearch implementation). Tenant scope + soft-delete exclusion come from the DbContext query filters.
public class MetadataSearchService : ISearchService
{
    private readonly SimplArchiveDbContext _dbContext;

    public MetadataSearchService(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // The fallback has no indexed ACL — the controller post-filters its hits by CanSee (ADR 0249).
    public bool PreFiltersByAcl => false;

    // Typed field + system-field filters are an OpenSearch capability; this degraded fallback ignores them
    // (ADR "Typed field filters in search", "System-field search").
    public async Task<SearchPage> SearchAsync(
        string query, Guid? repositoryId, SearchAccess access, SearchFilters filters,
        int skip, int take, CancellationToken cancellationToken)
    {
        var term = query.Trim().ToLowerInvariant();
        if (term.Length == 0)
        {
            return new SearchPage([], false);
        }

        var documents = _dbContext.Documents.AsQueryable();

        if (repositoryId is { } rootId)
        {
            var descendantIds = await CollectSubtreeIdsAsync(rootId, cancellationToken);
            documents = documents.Where(d => descendantIds.Contains(d.Id));
        }

        var matched = documents.Where(d =>
            d.Name.ToLower().Contains(term)
            || _dbContext.FieldValues.Any(fv => fv.DocumentId == d.Id && fv.Value.ToLower().Contains(term))
            || _dbContext.DocumentAnnotations.Any(a => a.DocumentId == d.Id && a.Text.ToLower().Contains(term)));

        var rows = await matched
            .OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Skip(skip)
            .Take(take + 1)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.ParentId,
                HasVersions = _dbContext.DocumentVersions.Any(v => v.DocumentId == d.Id),
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var hits = rows.Take(take).Select(d => new SearchCandidate(d.Id, d.Name, !d.HasVersions, d.ParentId)).ToList();
        return new SearchPage(hits, hasMore);
    }

    private async Task<HashSet<Guid>> CollectSubtreeIdsAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var all = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };

        while (frontier.Count > 0)
        {
            var children = await _dbContext.Documents
                .Where(d => d.ParentId != null && frontier.Contains(d.ParentId.Value))
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            frontier = children.Where(all.Add).ToList();
        }

        return all;
    }
}
