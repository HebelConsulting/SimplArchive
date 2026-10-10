using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// The folder-contents pane's ordering, split out of MainWindowViewModel (the 1000-line rule / ADR "no class
// exceeds 1000 lines"): folders-on-top then documents by the active criterion, the per-folder default, and the
// ephemeral column-header override. Document rows can order by (DocumentDate, DocumentTime) since ADR 0758.
public partial class MainWindowViewModel
{
    private void ApplyContentSort()
    {
        if (Items.Count < 2)
        {
            return;
        }

        // Folders on top (always alphabetical, issue #339), then documents in the folder's sort order: the shared
        // FolderContentsOrder, which the web and the phone answer identically. A column-header click is an explicit
        // ephemeral override of the whole list, and stays this client's.
        var folders = Items.Where(n => n.IsFolder);
        var docs = Items.Where(n => !n.IsFolder);
        var sorted = _headerSortActive
            ? HeaderSort(folders).Concat(HeaderSort(docs)).ToList()
            : FolderContentsOrder.Arrange(Items, _folderSortOrder,
                n => new FolderContentsOrder.Keys(n.IsFolder, n.DisplayName, n.DocumentDate, n.DocumentTime, n.VersionCreatedAt)).ToList();
        Items.Clear();
        foreach (var n in sorted)
        {
            Items.Add(n);
        }
    }

    private IEnumerable<NodeViewModel> HeaderSort(IEnumerable<NodeViewModel> items)
    {
        IEnumerable<NodeViewModel> ordered = _contentSortColumn switch
        {
            "type" => items.OrderBy(n => n.DocumentType, StringComparer.OrdinalIgnoreCase),
            "date" => items.OrderBy(n => n.DocumentDate ?? DateOnly.MinValue).ThenBy(n => n.DocumentTime ?? TimeOnly.MinValue),
            "size" => items.OrderBy(n => n.SizeBytes ?? -1),
            "tags" => items.OrderBy(n => n.TagsText, StringComparer.OrdinalIgnoreCase),
            "owner" => items.OrderBy(n => n.CreatedBy, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
        return _contentSortAscending ? ordered : ordered.Reverse();
    }
}
