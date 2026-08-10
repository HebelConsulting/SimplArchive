namespace SimplArchive.DesktopClient.ViewModels;

// A clickable crumb in the ribbon breadcrumb. FolderId is null for the "Repositories" root crumb.
public sealed class BreadcrumbViewModel
{
    public required string Name { get; init; }

    public required Guid? FolderId { get; init; }

    // The folder's advertised addresses, carried whole from the node the crumb was built from (ADR 0543, issue
    // #416) — opening a crumb needs `children` AND `references`, so carrying just one of them would only move
    // the extra fetch rather than remove it. Null for the "Repositories" root crumb, which lists repositories
    // rather than children, and for a crumb built from a node that advertised nothing; the caller then reads the
    // resource once and follows its rels — a round trip, never a composed path.
    public IReadOnlyDictionary<string, string>? Links { get; init; }

    // The "/" separator is drawn before every crumb except the first.
    public required bool ShowSeparator { get; init; }
}
