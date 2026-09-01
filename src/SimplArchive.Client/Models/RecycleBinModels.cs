using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>The tenant-wide recycle bin: what is deleted, and what may be done to the collection as a whole.</summary>
/// <remarks>
/// The envelope's own links carry the bulk actions — restore-selected, purge-selected, purge-all — because they
/// belong to the collection rather than to any row. Captured where the collection is read, so no caller has to
/// rebuild them (ADR 0557).
/// </remarks>
public record RecycleBinResponse
{
    public List<RecycleBinItem> Items { get; set; } = [];

    /// <summary>True when more items exist than the server will list in one response.</summary>
    public bool Truncated { get; set; }

    public List<LinkResponse> Links { get; set; } = [];

    public string? Href(string rel) => SimplArchive.Client.Hypermedia.Links.Href(Links, rel);
}

/// <summary>One deleted document, with everything the detail pane needs to read it.</summary>
/// <remarks>
/// The row advertises its own addresses — restore, purge, and the four the detail pane reads (mask, index-data,
/// chat, versions). They arrive with the listing and cost nothing, which is why the pane follows them instead of
/// spending a request per selection to rediscover addresses that were already known here (ADR 0557, and the
/// server half in #450).
/// </remarks>
public record RecycleBinItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Where it used to live, for telling two same-named documents apart.</summary>
    public string Path { get; set; } = string.Empty;

    public DateTimeOffset DeletedAt { get; set; }

    public string DeletedBy { get; set; } = string.Empty;

    public List<LinkResponse> Links { get; set; } = [];

    /// <summary>
    /// The address for <paramref name="rel"/>, or <c>null</c> when the server did not offer it — which means
    /// "not available to you, here, now", so the affordance is hidden rather than tried (ADR 0543).
    /// </summary>
    public string? Href(string rel) => SimplArchive.Client.Hypermedia.Links.Href(Links, rel);
}
