using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the references-of-an-item dialog: lists every folder that references a given item, each with its
// full path — see ADR "References-of-an-item list". Opening a row is handled by the view (it closes the
// dialog with the chosen folder id, which the main window then navigates to).
public sealed partial class ReferencesViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;
    private readonly Guid _itemId;

    public ReferencesViewModel(SimplArchiveApiClient api, Guid itemId, string itemName)
    {
        _api = api;
        _itemId = itemId;
        ItemName = itemName;
    }

    public string ItemName { get; }

    public ObservableCollection<ReferencingFolderViewModel> Items { get; } = [];

    [ObservableProperty] private string _status = "";

    public async Task LoadAsync()
    {
        Items.Clear();
        try
        {
            foreach (var folder in await _api.GetReferencingFoldersAsync(_itemId))
            {
                Items.Add(new ReferencingFolderViewModel { Id = folder.Id, Name = folder.Name, Path = folder.Path });
            }

            Status = Items.Count == 0
                ? "This item isn't referenced anywhere."
                : $"Referenced in {Items.Count} folder(s).";
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
        }
    }
}

// A row in the references dialog — a folder that references the item.
public sealed class ReferencingFolderViewModel
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }
}
