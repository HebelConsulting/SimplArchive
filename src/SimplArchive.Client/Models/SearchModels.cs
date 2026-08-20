using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>One search result — a document or a folder, with the server's highlighted snippet.</summary>
/// <remarks>
/// <see cref="Highlight"/> is HTML-escaped surrounding text carrying only <c>&lt;em&gt;</c> match tags, so the
/// tab renders it as markup; nothing else in it comes from the indexed content unescaped.
/// </remarks>
public record SearchHit
{
    /// <summary>The mask's icon token, or null for the generic folder/document glyph.</summary>
    /// <remarks>
    /// Carried so a hit wears the same icon here as in the tree — an object that changes shape depending on
    /// which pane found it reads as two different objects.
    /// </remarks>
    public string? Icon { get; set; }

    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public bool IsFolder { get; set; }

    public Guid? ParentId { get; set; }

    public string Path { get; set; } = "";

    public string Highlight { get; set; } = "";

    /// <summary>
    /// What this hit lets you do: <c>self</c> always, and <c>versions</c> for a document — the rel a preview
    /// follows to reach the current version's <c>preview</c> and <c>text-layout</c> addresses (#462).
    /// </summary>
    /// <remarks>
    /// The row carries its own addresses so previewing a hit follows what the listing advertised rather than
    /// re-resolving the document (ADR 0555/0557). A folder advertises no <c>versions</c>, which is how the tab
    /// knows there is nothing to preview — a missing rel means "not available here" (ADR 0543).
    /// </remarks>
    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>A page of search results, its facets, and the link to the next page.</summary>
public record SearchResponse
{
    public List<SearchHit> Results { get; set; } = [];

    /// <summary>The same across every page of one search, so only the first page's copy is kept.</summary>
    public FacetsDto? Facets { get; set; }

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>The drill-down dimensions the server offers for the current result set.</summary>
public record FacetsDto
{
    public List<FacetBucket> DocumentTypes { get; set; } = [];

    public List<FacetBucket> CreatedBy { get; set; } = [];

    public List<FacetBucket> Years { get; set; } = [];

    public List<FacetBucket> Tags { get; set; } = [];

    public List<FacetBucket> FileTypes { get; set; } = [];

    public List<FacetBucket> SensitivityLabels { get; set; } = [];

    /// <summary>Per-index-field facets, keyed by field name rather than being a fixed dimension.</summary>
    public List<FieldFacetDto> Fields { get; set; } = [];
}

/// <summary>One index field's facet dimension.</summary>
public record FieldFacetDto
{
    public string Name { get; set; } = "";

    public List<FacetBucket> Buckets { get; set; } = [];
}

/// <summary>One selectable facet value and how many results carry it.</summary>
public record FacetBucket
{
    public string Value { get; set; } = "";

    public long Count { get; set; }
}

/// <summary>The index fields available to build a field filter on.</summary>
public record SearchFieldsResponse
{
    public List<SearchFieldItem> Fields { get; set; } = [];
}

/// <summary>An index field the refinement panel can filter by, with the data type that picks its operators.</summary>
public record SearchFieldItem
{
    public string Name { get; set; } = "";

    public int DataType { get; set; }
}

/// <summary>The saved-search listing, whose envelope also carries the share-target picker's address.</summary>
public record SavedSearchesResponse
{
    public List<SavedSearchDto> SavedSearches { get; set; } = [];

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>A saved search — mine to edit and share, or someone else's that was shared with me.</summary>
public record SavedSearchDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string QueryString { get; set; } = "";

    public int ShareScope { get; set; }

    public bool IsMine { get; set; }

    public string OwnerName { get; set; } = "";

    public List<LinkResponse> Links { get; set; } = [];

    /// <summary>
    /// Only the owner's rows advertise self/delete/shares, so a search shared WITH you carries none of them and
    /// its actions disable from the server's answer rather than from <see cref="IsMine"/> (issue #416).
    /// </summary>
    public string? Href(string rel) => Links.FirstOrDefault(l => l.Rel == rel)?.Href.TrimStart('/');
}

/// <summary>The people and groups a saved search may be shared with.</summary>
public record ShareTargetsResponse
{
    public List<ShareTargetUserDto> Users { get; set; } = [];

    public List<ShareTargetGroupDto> Groups { get; set; } = [];
}

/// <summary>A user the share picker offers.</summary>
public record ShareTargetUserDto
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = "";
}

/// <summary>A group the share picker offers.</summary>
public record ShareTargetGroupDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>Who a saved search is currently shared with.</summary>
public record SharesResponse
{
    public List<ShareGrantDto> Shares { get; set; } = [];
}

/// <summary>One principal a saved search is shared with.</summary>
public record ShareGrantDto
{
    public string PrincipalType { get; set; } = "";

    public Guid PrincipalId { get; set; }
}
