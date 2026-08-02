using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the references-of-an-item dialog: the item's real primary location (where it actually lives) plus every
// folder that references it, each with its full path — see ADR "References-of-an-item list" and ADR 0506.
// Opening a row or promoting a reference is handled by the view (it closes the dialog with a result the main
// window acts on).
public sealed partial class ReferencesViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;

    public ReferencesViewModel(SimplArchiveApiClient api, Guid itemId, string itemName)
    {
        _api = api;
        ItemId = itemId;
        ItemName = itemName;
    }

    public Guid ItemId { get; }

    public string ItemName { get; }

    // The item's real home folder (ADR 0506) — null when it's a repository root or the caller can't see the
    // parent, in which case the primary row is hidden and no promote is offered.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimary))]
    private ReferencingFolderViewModel? _primaryLocation;

    public bool HasPrimary => PrimaryLocation is not null;

    public ObservableCollection<ReferencingFolderViewModel> Items { get; } = [];

    [ObservableProperty] private string _status = "";

    public async Task LoadAsync()
    {
        Items.Clear();
        try
        {
            var view = await _api.GetReferencesViewAsync(ItemId);
            PrimaryLocation = view.Primary is { } p
                ? new ReferencingFolderViewModel { Id = p.Id, Name = p.Name, Path = p.Path }
                : null;

            foreach (var folder in view.Folders)
            {
                Items.Add(new ReferencingFolderViewModel { Id = folder.Id, Name = folder.Name, Path = folder.Path });
            }

            Status = Items.Count == 0
                ? Strings.Get("NotReferenced")
                : string.Format(Strings.Get("RefReferencedInN"), Items.Count);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
        }
    }
}

// A row in the references dialog — a folder that references the item, or the item's own primary location.
public sealed class ReferencingFolderViewModel
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }
}

// The dialog's outcome: navigate to FolderId, and when Promote is set, first make it the item's primary location.
public sealed record ReferencesDialogResult(Guid FolderId, bool Promote);
