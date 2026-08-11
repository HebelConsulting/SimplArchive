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

    public List<LinkResponse>? Links { get; set; }
}
