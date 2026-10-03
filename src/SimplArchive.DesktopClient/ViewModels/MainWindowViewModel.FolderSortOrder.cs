using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// A folder's persisted contents sort order, as the detail pane shows and edits it (#941).
/// </summary>
/// <remarks>
/// <para>
/// A partial, by ADR 0733's rule as #941 states it: the edit is staged into the shell's own edit model and
/// committed by the shell's Save, so the cluster cannot stand alone.
/// </para>
/// <para>
/// <b>Sixth banner checked, sixth wrong — and this one hid FOUR concerns.</b> "Folder detail pane: the open
/// folder's persisted contents sort order" ran 121 lines. The sort order is the ~35 here. Under the same name
/// were the detail pane's GLYPH state (what icon and colour the subject is drawn with), the BREADCRUMB and its
/// navigation, and <c>CreateFolderAsync</c>. Each kept its members and gained a banner that describes them.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    public string FolderSortText => _folderSortOrder switch
    {
        1 => Strings.Get("FolderSortDocDate"),
        2 => Strings.Get("FolderSortCreated"),
        _ => Strings.Get("FolderSortName"),
    };

    private int _detailSortOrder;

    // Staged like every other edited field, so Cancel discards it and Save commits it with the rest.
    [ObservableProperty] private int _editSortOrder;

    public string DetailSortText => _detailSortOrder switch
    {
        1 => Strings.Get("FolderSortDocDate"),
        2 => Strings.Get("FolderSortCreated"),
        _ => Strings.Get("FolderSortName"),
    };

    // The tree context menu's "Contents sort order" entry: the order is a field in the ONE detail pane now
    // (issue #408), so this opens that edit rather than a mode of its own — one edit, one Save, wherever you
    // started from. The old toggle (its own Edit/Save/Cancel and its own save path) is gone with it.
    [RelayCommand]
    private void BeginFolderSortEdit() => BeginEditCommand.Execute(null);

    // The folder currently shown in the contents pane — the drop target for a drag onto empty space.
}
