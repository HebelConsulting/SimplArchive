namespace SimplArchive.DesktopClient.ViewModels;

// A clickable crumb in the ribbon breadcrumb. FolderId is null for the "Repositories" root crumb.
public sealed class BreadcrumbViewModel
{
    public required string Name { get; init; }

    public required Guid? FolderId { get; init; }

    // The "/" separator is drawn before every crumb except the first.
    public required bool ShowSeparator { get; init; }
}
