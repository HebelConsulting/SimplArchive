namespace SimplArchive.DesktopClient.ViewModels;

// A row in the Search tab's results list — see ADR "Metadata search (first slice)". ParentId is the item's
// home folder (null = a repository root); opening navigates to it in the Repositories workbench.
public sealed class SearchResultViewModel
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool IsFolder { get; init; }

    public required Guid? ParentId { get; init; }

    /// <summary>The hit's advertised addresses (`self`, `versions`, `parent`) — opening follows these (#443).</summary>
    public IReadOnlyDictionary<string, string>? Links { get; init; }

    public required string Path { get; init; }

    // A snippet with the matched terms wrapped in <em>…</em> (ADR "Search result highlighting"), or "" when
    // nothing textual matched. Rendered into bold runs by the InlineHighlighter attached property.
    public string Highlight { get; init; } = "";

    // The `versions` address this hit advertised, or null for a folder (which has nothing to preview). Carried
    // on the row so previewing follows what the listing handed over rather than re-resolving the document
    // (#462, ADRs 0543/0555/0557).
    public string? VersionsHref { get; init; }

    public string IconValue => IsFolder ? "mdi-folder" : "mdi-file-document-outline";
}
