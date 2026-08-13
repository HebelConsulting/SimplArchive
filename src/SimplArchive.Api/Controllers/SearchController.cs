using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Search;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Metadata search (ADR "Metadata search (first slice)", implementing part of ADR 0011/0043/0137): a
/// free-text query matched against document/folder names + index-field values, tenant-wide by default with
/// an optional repositoryId filter (ADR 0137), ACL-filtered per result. Behind ISearchService, so a later
/// slice can swap in OpenSearch full-text without changing this controller or the clients. Results are
/// cursor-paginated with the same per-item CanSee walk as RepositoriesController.List (a straight Take can't
/// compose with a post-query authorization filter).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/search")]
[Authorize]
public partial class SearchController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ISearchService _searchService;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public SearchController(
        SimplArchiveDbContext dbContext,
        ISearchService searchService,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        Documents.IClearanceScopeResolver clearanceScope)
    {
        _dbContext = dbContext;
        _searchService = searchService;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _clearanceScope = clearanceScope;
    }

    private readonly Documents.IClearanceScopeResolver _clearanceScope;

    // Plain mutable classes, not records — XmlSerializer (ADR "JSON/XML content negotiation") needs a
    // parameterless constructor and settable properties.
    public class SearchResultResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public bool IsFolder { get; set; }

        // The item's home folder (Document.ParentId) — null if it's a repository root. Lets the client open
        // the containing folder and select the item.
        public Guid? ParentId { get; set; }

        // Full display path, e.g. "Repositories / Contracts / Invoice 42".
        public string Path { get; set; } = "";

        // A snippet with the matched terms wrapped in <em>…</em> (ADR "Search result highlighting") — a content
        // excerpt, else a matched index-field value, else the name; empty when nothing textual matched or the
        // metadata fallback is in use. Surrounding text is HTML-escaped, so the only markup is the <em> tags.
        public string Highlight { get; set; } = "";
    }

    // The rels a hit hands out, so a client that wants to DO something with a result never composes a URL
    // (ADR 0543). `versions` is what a preview needs: it resolves to the current version, which carries the
    // `preview` and `text-layout` rels the renderer follows — the same two-step the contents list already
    // uses (DocumentsController's child rows advertise `versions` for exactly this reason).
    //
    // A folder gets only `self`: there is nothing to preview, and advertising a rel that leads nowhere would
    // make the client offer an affordance the server cannot honour. A missing rel means "not available here"
    // (ADR 0543), which for a folder is precisely true.
    private static List<Link> BuildHitLinks(Guid id, bool isFolder) =>
        isFolder
            ? [new Link("self", $"/api/documents/{id}", "GET")]
            : [
                new Link("self", $"/api/documents/{id}", "GET"),
                new Link("versions", $"/api/documents/{id}/versions", "GET"),
              ];

    public class SearchResultsResource : HypermediaResource
    {
        public List<SearchResultResource> Results { get; set; } = [];

        // Facet counts for refinement (ADR "Search facets") — null on the Postgres fallback / an empty query.
        public SearchFacetsResource? Facets { get; set; }
    }

    public class SearchFacetsResource
    {
        public List<FacetBucketResource> DocumentTypes { get; set; } = [];
        public List<FacetBucketResource> CreatedBy { get; set; } = [];
        public List<FacetBucketResource> Years { get; set; } = [];
        public List<FacetBucketResource> Tags { get; set; } = [];
        // File type + per-Select-index-field facets (ADR "Search facet refinements").
        public List<FacetBucketResource> FileTypes { get; set; } = [];
        // Data-classification sensitivity label (ADR "Sensitivity-label list badge + search facet"); drill-down
        // reuses the existing system[sensitivityLabel][in] filter.
        public List<FacetBucketResource> SensitivityLabels { get; set; } = [];
        public List<FieldFacetResource> Fields { get; set; } = [];
    }

    public class FieldFacetResource
    {
        public string Name { get; set; } = "";
        public List<FacetBucketResource> Buckets { get; set; } = [];
    }

    public class FacetBucketResource
    {
        public string Value { get; set; } = "";
        public long Count { get; set; }
    }

    // Free-text query only in this slice (ADR 0043's typed field filters come later). ?q= is the term,
    // optional ?repositoryId= narrows to one repository (ADR 0137), ?cursor=&limit= paginate.
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? q, [FromQuery] Guid? repositoryId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var pageSize = PageSize.Resolve(limit);
        var filterSuffix = FilterQuerySuffix();

        var fieldFilters = await BuildFieldFiltersAsync(cancellationToken);
        var systemFilters = BuildSystemFilters();
        var filters = new SearchFilters(fieldFilters, systemFilters);

        if (string.IsNullOrWhiteSpace(q) && filters.IsEmpty)
        {
            return Ok(new SearchResultsResource
            {
                Links = [new Link("self", Url.Action(nameof(Search), new { q, repositoryId, cursor, limit = pageSize })!, "GET")],
            });
        }

        // Ranked results come back paged by a simple integer offset (relevance order can't use a keyset
        // cursor). The caller's SearchAccess (ADR "Indexed ACL in search") lets the OpenSearch path pre-filter
        // by indexed visibility — hits are already authorized, paging is exact. The metadata fallback doesn't
        // pre-filter, so each hit is still post-filtered by CanSee (ADR 0137) and a page may hold fewer than
        // `limit` visible hits.
        var skip = int.TryParse(cursor, out var offset) && offset >= 0 ? offset : 0;

        var access = await GetSearchAccessAsync(cancellationToken);

        // Data-classification clearance (ADR "Sensitivity clearance enforcement"): a restricted caller gets a
        // rank ceiling so the OpenSearch path drops over-clearance hits (the metadata fallback drops them via
        // the per-hit CanSee post-filter, which the calculator already clearance-enforces). Admins/BypassAcl and
        // unenforced tenants get no ceiling.
        var clearance = await _clearanceScope.ResolveAsync(cancellationToken);
        if (!clearance.IsUnrestricted && !access.BypassAcl)
        {
            access = access with { MaxSensitivityRank = clearance.MaxRank };
        }

        // The Select-type index fields to compute per-field facets over (ADR "Search facet refinements").
        var facetFields = await GetFacetableFieldNamesAsync(cancellationToken);
        var page = await _searchService.SearchAsync(q ?? "", repositoryId, access, filters with { FacetFields = facetFields }, skip, pageSize, cancellationToken);

        var visible = new List<SearchResultResource>();
        foreach (var hit in page.Hits)
        {
            if (!_searchService.PreFiltersByAcl && !await CanSeeAsync(hit.Id, cancellationToken))
            {
                continue;
            }

            // A stale index hit can point at a document that's since been soft-deleted — the async reindex
            // removes it shortly after (ADR 0255), but the index can lag. Skip such a hit rather than 500ing the
            // whole search (BuildPathAsync returns null when the document/an ancestor is no longer readable).
            var path = await BuildPathAsync(hit.Id, cancellationToken);
            if (path is null)
            {
                continue;
            }

            visible.Add(new SearchResultResource
            {
                Id = hit.Id,
                Name = hit.Name,
                IsFolder = hit.IsFolder,
                ParentId = hit.ParentId,
                Path = path,
                Highlight = hit.Highlight ?? "",
                Links = BuildHitLinks(hit.Id, hit.IsFolder),
            });
        }

        // The typed field filters live in fields[..][..] query params that Url.Action can't round-trip, so
        // they're appended to the generated hrefs — otherwise `next` would drop the filters mid-pagination.
        var links = new List<Link> { new("self", Url.Action(nameof(Search), new { q, repositoryId, cursor, limit = pageSize })! + filterSuffix, "GET") };

        if (page.HasMore)
        {
            links.Add(new Link("next", Url.Action(nameof(Search), new { q, repositoryId, cursor = (skip + pageSize).ToString(), limit = pageSize })! + filterSuffix, "GET"));
        }

        return Ok(new SearchResultsResource { Results = visible, Facets = BuildFacets(page.Facets), Links = links });
    }

    private static SearchFacetsResource? BuildFacets(SearchFacets? facets)
    {
        if (facets is null)
        {
            return null;
        }

        static List<FacetBucketResource> Map(IReadOnlyList<SearchFacetBucket> buckets) =>
            buckets.Select(b => new FacetBucketResource { Value = b.Value, Count = b.Count }).ToList();

        return new SearchFacetsResource
        {
            DocumentTypes = Map(facets.DocumentTypes),
            CreatedBy = Map(facets.CreatedBy),
            Years = Map(facets.Years),
            Tags = Map(facets.Tags),
            FileTypes = Map(facets.FileTypes),
            SensitivityLabels = Map(facets.SensitivityLabels),
            Fields = facets.Fields.Select(f => new FieldFacetResource { Name = f.Name, Buckets = Map(f.Buckets) }).ToList(),
        };
    }

    // The Select-type index-field names (across the current mask versions) to compute per-field facets over
    // (ADR "Search facet refinements") — categorical fields with bounded cardinality make natural facets.
    private async Task<List<string>> GetFacetableFieldNamesAsync(CancellationToken cancellationToken)
    {
        var names = await (
            from fieldDefinition in _dbContext.FieldDefinitions
            join maskVersion in _dbContext.MaskVersions on fieldDefinition.MaskVersionId equals maskVersion.Id
            where maskVersion.IsCurrent
                && (fieldDefinition.DataType == FieldDataType.SingleSelect || fieldDefinition.DataType == FieldDataType.MultiSelect)
            select fieldDefinition.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        return names.OrderBy(n => n).ToList();
    }

    // Matches a typed field-filter query key: fields[<name>][<op>].
    [GeneratedRegex(@"^fields\[(?<name>[^\]]+)\]\[(?<op>[^\]]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldFilterKeyRegex();

    // Matches a system-field filter query key: system[<name>][<op>].
    [GeneratedRegex(@"^system\[(?<name>[^\]]+)\]\[(?<op>[^\]]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SystemFilterKeyRegex();

    // The fixed indexed system fields (ADR "System-field search") and their filter kind. Dates take
    // eq/gt/gte/lt/lte; the resolved-name keyword fields take eq/contains/in.
    private static readonly Dictionary<string, (string Field, FieldFilterKind Kind)> SystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["createdAt"] = ("createdAt", FieldFilterKind.Date),
        ["versionCreatedAt"] = ("versionCreatedAt", FieldFilterKind.Date),
        ["documentDate"] = ("documentDate", FieldFilterKind.Date),
        ["createdBy"] = ("createdBy", FieldFilterKind.Text),
        ["versionCreatedBy"] = ("versionCreatedBy", FieldFilterKind.Text),
        // The document type (assigned mask name) — a facet drill-down field (ADR "Search facets").
        ["documentType"] = ("documentType", FieldFilterKind.Text),
        // The sensitivity label name (ADR "Data classification / sensitivity labels").
        ["sensitivityLabel"] = ("sensitivityLabel", FieldFilterKind.Text),
        // Free-form tags (ADR "Document tags") — a facet drill-down field over the `tags` keyword array.
        ["tag"] = ("tags", FieldFilterKind.Text),
        // File type + document-date year (ADR "Search facet refinements") — keyword facet drill-down fields.
        ["fileType"] = ("fileType", FieldFilterKind.Text),
        ["documentYear"] = ("documentYear", FieldFilterKind.Text),
    };

    private static readonly Dictionary<FieldFilterKind, HashSet<string>> SystemOperatorsByKind = new()
    {
        [FieldFilterKind.Date] = ["eq", "gt", "gte", "lt", "lte"],
        [FieldFilterKind.Text] = ["eq", "contains", "in"],
    };

    // Parses ?system[Field][op]=value into SystemFilters (ADR "System-field search"). The field set is fixed
    // (no DB lookup); validates the field name, the operator against its kind, and date parseability.
    private List<SystemFilter> BuildSystemFilters()
    {
        var raw = Request.Query
            .Select(kv => (Key: SystemFilterKeyRegex().Match(kv.Key), kv.Value))
            .Where(x => x.Key.Success)
            .Select(x => (Name: x.Key.Groups["name"].Value, Op: x.Key.Groups["op"].Value.ToLowerInvariant(), Value: x.Value.ToString()))
            .ToList();

        var filters = new List<SystemFilter>();
        foreach (var (name, op, value) in raw)
        {
            if (!SystemFields.TryGetValue(name, out var meta))
            {
                throw new UnknownSystemFieldException($"'{name}' is not a system field (known: {string.Join(", ", SystemFields.Keys)}).");
            }

            if (!SystemOperatorsByKind[meta.Kind].Contains(op))
            {
                throw new InvalidFilterOperatorException($"Operator '{op}' is not valid for system field '{name}'.");
            }

            var values = op == "in"
                ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [value];

            if (values.Length == 0)
            {
                throw new InvalidFilterValueException($"System filter '{name}' has no value.");
            }

            if (meta.Kind == FieldFilterKind.Date)
            {
                foreach (var v in values)
                {
                    if (!DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                    {
                        throw new InvalidFilterValueException($"'{v}' is not a valid date for system field '{name}'.");
                    }
                }
            }

            filters.Add(new SystemFilter(meta.Field, meta.Kind, op, values));
        }

        return filters;
    }

    private static readonly Dictionary<FieldFilterKind, HashSet<string>> OperatorsByKind = new()
    {
        [FieldFilterKind.Text] = ["eq", "contains"],
        [FieldFilterKind.Number] = ["eq", "gt", "gte", "lt", "lte"],
        [FieldFilterKind.Date] = ["eq", "gt", "gte", "lt", "lte"],
        [FieldFilterKind.Boolean] = ["eq"],
        [FieldFilterKind.Select] = ["eq", "in"],
    };

    // Parses ?fields[Name][op]=value into typed FieldFilters (ADR 0043). Resolves each field name to its
    // DataType via the FieldDefinition (tenant-scoped; the deterministically-first definition wins if a name
    // repeats across masks with different types), validates the operator against the type and the value's
    // parseability, and throws a 4xx ApiException on anything malformed.
    private async Task<List<FieldFilter>> BuildFieldFiltersAsync(CancellationToken cancellationToken)
    {
        var raw = Request.Query
            .Select(kv => (Key: FieldFilterKeyRegex().Match(kv.Key), kv.Value))
            .Where(x => x.Key.Success)
            .Select(x => (Name: x.Key.Groups["name"].Value, Op: x.Key.Groups["op"].Value.ToLowerInvariant(), Value: x.Value.ToString()))
            .ToList();

        if (raw.Count == 0)
        {
            return [];
        }

        var names = raw.Select(r => r.Name).Distinct().ToList();
        var definitions = await _dbContext.FieldDefinitions
            .Where(fd => names.Contains(fd.Name))
            .Select(fd => new { fd.Name, fd.DataType, fd.CreatedAt, fd.Id })
            .ToListAsync(cancellationToken);

        var kindByName = definitions
            .GroupBy(d => d.Name)
            .ToDictionary(g => g.Key, g => MapKind(g.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).First().DataType));

        var filters = new List<FieldFilter>();
        foreach (var (name, op, value) in raw)
        {
            if (!kindByName.TryGetValue(name, out var kind))
            {
                throw new UnknownFilterFieldException($"No index field named '{name}' exists.");
            }

            if (!OperatorsByKind[kind].Contains(op))
            {
                throw new InvalidFilterOperatorException($"Operator '{op}' is not valid for a {kind} field.");
            }

            var values = op == "in"
                ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [value];

            if (values.Length == 0)
            {
                throw new InvalidFilterValueException($"Filter '{name}' has no value.");
            }

            foreach (var v in values)
            {
                if (!ValueParses(kind, v))
                {
                    throw new InvalidFilterValueException($"'{v}' is not a valid {kind} value for '{name}'.");
                }
            }

            filters.Add(new FieldFilter(name, kind, op, values));
        }

        return filters;
    }

    private static FieldFilterKind MapKind(FieldDataType dataType) => dataType switch
    {
        FieldDataType.Number => FieldFilterKind.Number,
        FieldDataType.Date => FieldFilterKind.Date,
        FieldDataType.Boolean => FieldFilterKind.Boolean,
        FieldDataType.SingleSelect or FieldDataType.MultiSelect => FieldFilterKind.Select,
        _ => FieldFilterKind.Text,
    };

    private static bool ValueParses(FieldFilterKind kind, string value) => kind switch
    {
        FieldFilterKind.Number => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _),
        FieldFilterKind.Date => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
        FieldFilterKind.Boolean => bool.TryParse(value, out _),
        _ => true,
    };

    // Rebuilds the raw fields[..][..] / system[..][..] query params so they can be appended to pagination
    // hrefs (Url.Action can't round-trip them) — otherwise `next` would drop the filters mid-pagination.
    private string FilterQuerySuffix()
    {
        var builder = new StringBuilder();
        foreach (var kv in Request.Query)
        {
            if (FieldFilterKeyRegex().IsMatch(kv.Key) || SystemFilterKeyRegex().IsMatch(kv.Key))
            {
                builder.Append('&').Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value.ToString()));
            }
        }

        return builder.ToString();
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => NoContent();

    public class SearchFieldResource : HypermediaResource
    {
        public string Name { get; set; } = "";

        // The FieldDataType as an integer (Text=0, Number=1, Date=2, Boolean=3, SingleSelect=4, MultiSelect=5),
        // consistent with every other enum on this Api — lets the client pick the right operators/input.
        public int DataType { get; set; }
    }

    public class SearchFieldsResource : HypermediaResource
    {
        public List<SearchFieldResource> Fields { get; set; } = [];
    }

    // Enumerates the tenant's distinct index-field names + types (across the current mask versions) so the
    // search-refinement UI can offer a field picker (ADR "Search-refinement UI"). A name that repeats across
    // masks with different types resolves to its first definition's type — matching BuildFieldFiltersAsync.
    [HttpGet("fields")]
    public async Task<IActionResult> Fields(CancellationToken cancellationToken)
    {
        var definitions = await (
            from fieldDefinition in _dbContext.FieldDefinitions
            join maskVersion in _dbContext.MaskVersions on fieldDefinition.MaskVersionId equals maskVersion.Id
            where maskVersion.IsCurrent
            select new { fieldDefinition.Name, fieldDefinition.DataType, fieldDefinition.CreatedAt, fieldDefinition.Id })
            .ToListAsync(cancellationToken);

        var fields = definitions
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .GroupBy(f => f.Name)
            .Select(g => new SearchFieldResource { Name = g.Key, DataType = (int)g.First().DataType })
            .OrderBy(f => f.Name)
            .ToList();

        return Ok(new SearchFieldsResource { Fields = fields, Links = [new Link("self", "/api/search/fields", "GET")] });
    }

    [HttpHead("fields")]
    public IActionResult HeadFields() => NoContent();

    // Builds an item's full display path (Repositories / … / item) by walking up ParentId.
    // Returns null when the document or any ancestor is no longer in the readable set (e.g. soft-deleted) — a
    // stale index hit the caller (SearchController.Search) skips rather than surfacing.
    private async Task<string?> BuildPathAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        Guid? currentId = documentId;

        while (currentId is { } id)
        {
            var node = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Name, d.ParentId })
                .SingleOrDefaultAsync(cancellationToken);
            if (node is null)
            {
                return null;
            }

            names.Add(node.Name);
            currentId = node.ParentId;
        }

        names.Reverse();
        return string.Join(" / ", names.Prepend("Repositories"));
    }

    // The caller's indexed-ACL context (ADR "Indexed ACL in search") — ServiceAccount first, then a
    // logged-in User (mutually exclusive per request). SearchAccess.None if neither is set.
    private async Task<SearchAccess> GetSearchAccessAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _effectiveRightsCalculator.GetSearchAccessForServiceAccountAsync(serviceAccountId, cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return await _effectiveRightsCalculator.GetSearchAccessForUserAsync(userId, cancellationToken);
        }

        return SearchAccess.None;
    }

    // Checks ServiceAccount first, then a logged-in User — the two accessors are mutually exclusive per
    // request. See ADR "Document-scope authorization retrofit for User".
    private async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken)).CanSee;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee;
        }

        return false;
    }
}
