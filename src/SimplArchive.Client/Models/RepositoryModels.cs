using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>A page of the repositories the caller can see, with the link to the next page.</summary>
/// <remarks>
/// Shared because two surfaces walk the same listing: the workbench tree builds its roots from it, and the
/// Search tab's repository scope picker fills its options from it (ADR 0558).
/// </remarks>
public record RepositoryListResponse
{
    public List<RepositorySummary> Repositories { get; set; } = [];

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>One repository row — a document with no parent (ADR 0200).</summary>
public record RepositorySummary
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public bool HasChildren { get; set; }

    public bool HasVersions { get; set; }

    public bool HasSubfolders { get; set; }

    /// <summary>What may be created in this repository, with the address for each (#673).</summary>
    /// <remarks>
    /// On the row, because a tree's top-level nodes get their whole menu from this listing and nothing
    /// re-fetches a node to fill one in — the same reason `create-child` is on it (issue #416).
    /// </remarks>
    public List<CreatableChild> Admits { get; set; } = [];

    /// <summary>The mask's icon token, or null for the generic glyph.</summary>
    public string? Icon { get; set; }

    public List<LinkResponse>? Links { get; set; }
}
