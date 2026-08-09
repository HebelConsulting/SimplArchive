namespace SimplArchive.DesktopClient.ViewModels;

// A clickable crumb in the ribbon breadcrumb. FolderId is null for the "Repositories" root crumb.
public sealed class BreadcrumbViewModel
{
    public required string Name { get; init; }

    public required Guid? FolderId { get; init; }

    // The address to LIST this folder's contents, carried from the tree node the crumb was built from (ADR 0543,
    // issue #416). Null for the "Repositories" root crumb, which lists repositories rather than children, and for
    // a crumb built from a node that advertised nothing. When it is null the caller falls back to fetching the
    // resource and following its rel — a round trip, never a composed path.
    public string? ChildrenHref { get; init; }

    // The "/" separator is drawn before every crumb except the first.
    public required bool ShowSeparator { get; init; }
}
