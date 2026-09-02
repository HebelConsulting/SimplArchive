using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Legal holds (ADR "Legal hold & retention enforcement"): the matters, the items each one freezes, creating a
// matter, releasing it, removing an item, and going to a held document.
//
// ReloadCurrentFolderAsync comes with them rather than staying in the shell: its ONLY three callers are the
// place, release and remove paths here, each refreshing the lock indicator after the hold changes. A helper
// whose entire caller set is one subject belongs with that subject.
//
// What did NOT come is TakeOverPersonalSpaceAsync, which sat in the MIDDLE of this section -- between loading
// the holds and creating one -- and is an admin granting themselves rights on a user's space (ADR 0672). It
// has gone to ItemActions, which is where the tree's context menu lives, because that is the only thing that
// invokes it.
//
// Worth noting for the next pass: the decay here was MID-SECTION, not at the tail. The previous two headings
// drifted at their ends (#941); this one had a stray dropped into its middle, which reading only the first or
// last members would have missed either way.
public sealed partial class MainWindowViewModel
{
    // Gates the Legal Holds tab + the place/release actions (set from whoami on login).
    [ObservableProperty] private bool _canLegalHold;

    public ObservableCollection<LegalHoldRowViewModel> LegalHolds { get; } = [];
    public ObservableCollection<LegalHoldItemRowViewModel> SelectedHoldItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHold))]
    [NotifyPropertyChangedFor(nameof(SelectedHoldIsActive))]
    private LegalHoldRowViewModel? _selectedLegalHold;

    public bool HasSelectedHold => SelectedLegalHold is not null;
    public bool SelectedHoldIsActive => SelectedLegalHold is { IsActive: true };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHoldItem))]
    private LegalHoldItemRowViewModel? _selectedHoldItem;

    public bool HasSelectedHoldItem => SelectedHoldItem is not null;

    // Go to the held document in Repositories (review finding) — addressed from the ROW's advertised
    // `document`/`parent` (ADR 0555/0559), never from pane state or a bare id.
    [RelayCommand]
    public async Task GoToHoldItemAsync(LegalHoldItemRowViewModel row)
    {
        SelectedTab = 0;
        var documentHref = row.Item.Href("document")
            ?? throw new InvalidOperationException($"The hold item '{row.DocumentName}' advertised no 'document' rel (ADR 0543/0555).");
        if (row.Item.Href("parent") is { } parentHref)
        {
            await RevealDocumentInTreeAsync(row.DocumentId, documentHref, parentHref);
        }
        else
        {
            // A document filed at a repository root is itself a top-level tree node.
            await RevealFolderInTreeAsync(documentHref);
        }
    }

    [RelayCommand]
    private async Task GoToSelectedHoldItemAsync()
    {
        if (SelectedHoldItem is { } row)
        {
            await GoToHoldItemAsync(row);
        }
    }

    [RelayCommand]
    public async Task LoadLegalHoldsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var holds = await _api.LegalHolds.GetLegalHoldsAsync();
            var previousId = SelectedLegalHold?.Id;
            LegalHolds.Clear();
            foreach (var h in holds)
            {
                LegalHolds.Add(new LegalHoldRowViewModel(h.Id, h.Name, h.IsActive, h.ItemCount, h));
            }

            SelectedLegalHold = LegalHolds.FirstOrDefault(h => h.Id == previousId);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadHolds");
        }
    }

    async partial void OnSelectedLegalHoldChanged(LegalHoldRowViewModel? value)
    {
        SelectedHoldItems.Clear();
        SelectedHoldItem = null; // the items are re-fetched — a held-over selection is a stale subject (ADR 0559)
        if (_api is null || value is null)
        {
            return;
        }

        try
        {
            var hold = await _api.LegalHolds.GetLegalHoldAsync(value.Hold);
            foreach (var item in hold.Items)
            {
                SelectedHoldItems.Add(new LegalHoldItemRowViewModel(item.DocumentId, item.DocumentName, item));
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    // Creates a new matter (optionally covering a document) — the (name, reason) come from the dialog.

    public async Task<bool> CreateLegalHoldAsync(string name, string? reason, Guid? documentId)
    {
        if (_api is null)
        {
            return false;
        }

        try
        {
            var hold = await _api.LegalHolds.CreateLegalHoldAsync(name, reason);
            if (documentId is { } docId)
            {
                await _api.LegalHolds.AddLegalHoldItemAsync(hold, docId);
                await ReloadCurrentFolderAsync(); // refresh the lock indicator
            }

            Status = string.Format(Strings.Get("StHoldCreated"), name);
            await LoadLegalHoldsAsync();
            return true;
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
            return false;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrCreateHold");
            return false;
        }
    }

    public async Task ReleaseSelectedHoldAsync()
    {
        if (_api is null || SelectedLegalHold is not { } hold)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.ReleaseLegalHoldAsync(hold.Hold);
            Status = Strings.Get("StHoldReleased");
            await LoadLegalHoldsAsync();
            await ReloadCurrentFolderAsync();
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrReleaseHold");
        }
    }

    [RelayCommand]
    public async Task RemoveHoldItemAsync(LegalHoldItemRowViewModel row)
    {
        if (_api is null || SelectedLegalHold is not { } hold)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.RemoveLegalHoldItemAsync(row.Item);
            var reselect = hold;
            await LoadLegalHoldsAsync();
            SelectedLegalHold = LegalHolds.FirstOrDefault(h => h.Id == reselect.Id);
            OnSelectedLegalHoldChanged(SelectedLegalHold);
            await ReloadCurrentFolderAsync();
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRemoveFromHold");
        }
    }

    private async Task ReloadCurrentFolderAsync()
    {
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId);
        }
    }
}
