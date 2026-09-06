using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The generic action surface (ADR 0743): a link the server labeled renders as a button in the detail pane
// with no client knowledge of the rel — how a module's actions (accept-aircraft, submit, …) reach this
// client without its code ever naming them. The label is server-rendered and localized; the rel's presence
// is the affordance (ADR 0543), so there is nothing to disable — an unavailable action simply is not here.
public partial class MainWindowViewModel
{
    /// <summary>The selected document's labeled actions, rebuilt on every detail load.</summary>
    public ObservableCollection<DocumentsClient.GenericActionInfo> DetailGenericActions { get; } = [];

    public bool HasDetailGenericActions => DetailGenericActions.Count > 0;

    // Rebuild rather than mutate, and CLEARED when the subject changes (ADR 0559): an action inherited from
    // the previously selected document would execute against the wrong subject.
    internal void SetDetailGenericActions(IReadOnlyList<DocumentsClient.GenericActionInfo>? actions)
    {
        DetailGenericActions.Clear();
        // The populate hook is NOT a button (ADR 0764): it fires on every interaction — open, selection, a
        // leaf click — so a manual trigger would be the one redundant control on the row.
        foreach (var action in (actions ?? []).Where(a => !IsAutoRefresh(a.Rel)))
        {
            DetailGenericActions.Add(action);
        }

        OnPropertyChanged(nameof(HasDetailGenericActions));
    }

    /// <summary>Whether a rel is the populate-on-open hook (ADR 0756) — a transition the client also invokes
    /// automatically when the folder is opened, so it must reload the folder CONTENTS, not just re-read the
    /// detail's own actions.</summary>
    private static bool IsAutoRefresh(string rel) => rel.StartsWith("machine-auto-refresh:", StringComparison.Ordinal);

    /// <summary>
    /// The populate-on-open hook (ADR 0756): when the just-opened folder advertises a machine-auto-refresh
    /// rel (now in <see cref="DetailGenericActions"/> after the open-folder detail load), POST it — a module
    /// stages fresh content under the folder — and reload the contents so it appears. The reload is a
    /// same-folder reload (<c>isReload</c> is derived true), so it cannot re-enter this hook and loop.
    /// </summary>
    private async Task AutoRefreshOpenFolderAsync()
    {
        if (_api is null || _currentFolderId is not { } id)
        {
            return;
        }

        var autoRefresh = AutoRefreshActions();
        if (autoRefresh.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var action in autoRefresh)
            {
                await _api.Documents.ExecuteActionAsync(action);
            }

            await LoadFolderContentsAsync(id, _currentFolderLinks); // same folder → a reload, no re-trigger
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
        }
    }

    [RelayCommand]
    private async Task ExecuteGenericAction(DocumentsClient.GenericActionInfo? action)
    {
        if (action is null || _api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.ExecuteActionAsync(action);
            Status = action.Label;

            // An auto-refresh action (ADR 0756) staged fresh content under the OPEN FOLDER — reload its
            // contents so it appears, the same as the on-open path; a manual Refresh must show new data too.
            if (IsAutoRefresh(action.Rel) && _currentFolderId is { } openFolder)
            {
                await LoadFolderContentsAsync(openFolder, _currentFolderLinks);
                return;
            }

            // The action changed the subject's state, so its rels — including this surface — are stale;
            // re-reading the resource is what makes a state transition's NEW actions appear (ADR 0550).
            if (_detailLinks is { } links && links.TryGetValue("self", out var selfHref))
            {
                var detail = await _api.Documents.GetDocumentDetailAsync(selfHref);
                SetDetailGenericActions(detail.GenericActions);
            }
        }
        catch (ApiActionException e)
        {
            // The problem detail is the explanation (ADR 0742: a diagnosis, not a verdict).
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(e.Message);
        }
    }

    /// <summary>The populate rels from the raw link map (they are filtered OUT of the action buttons —
    /// ADR 0764), shaped as executable actions.</summary>
    private List<DocumentsClient.GenericActionInfo> AutoRefreshActions() =>
        (_detailLinks ?? new Dictionary<string, string>())
            .Where(kv => IsAutoRefresh(kv.Key))
            .Select(kv => new DocumentsClient.GenericActionInfo(kv.Key, string.Empty, "POST", kv.Value))
            .ToList();

    // A per-href cooldown keeps rapid clicking in the weather area from hammering the provider, and doubles
    // as the recursion brake for the reload after a refresh (ADR 0764).
    private static readonly Dictionary<string, DateTimeOffset> _autoRefreshedAt = [];
    private static readonly TimeSpan AutoRefreshCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The populate hook follows every interaction (ADR 0764): SELECTING a subject whose resource carries a
    /// machine-auto-refresh rel — the weather folder, or a leaf carrying its parent's — re-fetches, then
    /// re-reads what is on screen. CanSee is enough (the update is automated; the module principal writes).
    /// </summary>
    private async Task AutoRefreshSelectionAsync(NodeViewModel document)
    {
        if (_api is null)
        {
            return;
        }

        var due = AutoRefreshActions()
            .Where(a => !_autoRefreshedAt.TryGetValue(a.Href, out var at) || DateTimeOffset.UtcNow - at >= AutoRefreshCooldown)
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var action in due)
            {
                _autoRefreshedAt[action.Href] = DateTimeOffset.UtcNow;
                await _api.Documents.ExecuteActionAsync(action);
            }
        }
        catch (ApiActionException)
        {
            return; // a refused populate is not worth interrupting a selection
        }

        if (_selectedDocumentId != document.Id)
        {
            return; // the user moved on (ADR 0559)
        }

        // The content may have been replaced: re-read what is on screen (the cooldown makes this a plain
        // read), and the listed children too when the subject IS the open folder.
        if (_currentFolderId is { } open && open == document.Id)
        {
            await LoadFolderContentsAsync(open, _currentFolderLinks);
        }

        await LoadDetailAsync(document);
    }
}
