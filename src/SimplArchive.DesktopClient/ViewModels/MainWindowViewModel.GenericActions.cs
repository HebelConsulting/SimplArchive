using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

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

    /// <summary>The selected document's module actions — pick-then-act surfaces (core ADR 0786).</summary>
    public ObservableCollection<DocumentsClient.ModuleActionInfo> DetailModuleActions { get; } = [];

    public bool HasDetailModuleActions => DetailModuleActions.Count > 0;

    // Same rebuild-and-clear rule as the labeled actions (ADR 0559): an action inherited from the previous
    // subject would fetch its choices for one document and commit them against another.
    internal void SetDetailModuleActions(IReadOnlyList<DocumentsClient.ModuleActionInfo>? actions)
    {
        DetailModuleActions.Clear();
        foreach (var action in actions ?? [])
        {
            DetailModuleActions.Add(action);
        }

        OnPropertyChanged(nameof(HasDetailModuleActions));
    }

    /// <summary>
    /// Runs a module action: fetch its choices, let the user pick one, send it back.
    /// </summary>
    /// <remarks>
    /// Addressed entirely from the ACTION the button carries (ADR 0559), never from the pane's loaded state —
    /// the hrefs came with the document the user clicked, so a detail still loading cannot redirect the
    /// commit at the previous subject.
    /// </remarks>
    [RelayCommand]
    private async Task RunModuleActionAsync(DocumentsClient.ModuleActionInfo? action)
    {
        if (action is null || _api is null)
        {
            return;
        }

        try
        {
            var options = await _api.Documents.GetModuleActionOptionsAsync(action.OptionsHref);
            var picker = new ModuleActionPickerViewModel(action, options);
            if (await ShowModuleActionPickerAsync?.Invoke(picker)! is not true || picker.Result is not { } chosen)
            {
                return;
            }

            await _api.Documents.InvokeModuleActionAsync(action.CommitHref, action.ValueField, chosen);
            Status = string.Format(CultureInfo.CurrentCulture, Strings.Get("ModuleActionDone"), action.Label);
            await ReloadDetailAsync();
        }
        catch (ApiActionException failure)
        {
            // The module's own sentence, already localized — shown rather than replaced with a generic
            // apology, because it is the one that says what to do next.
            Status = failure.Message;
        }
    }

    /// <summary>
    /// Opens the picker. A settable callback rather than a constructor argument (ADR 0730): the dialog needs
    /// a view that does not exist when the view-model is built, and a forgotten one disables a visible
    /// button — loud, not silent.
    /// </summary>
    public Func<ModuleActionPickerViewModel, Task<bool>>? ShowModuleActionPickerAsync { get; set; }

    /// <summary>One Status row for the pane (#1062): the pretty name and, when unmet, the diagnoses. The
    /// display split lives in SimplArchive.Presentation so both clients answer identically (ADR 0650).</summary>
    public sealed record MachineStatusRow(string DisplayName, bool Satisfied, IReadOnlyList<string> Failures);

    public ObservableCollection<MachineStatusRow> DetailMachineStatuses { get; } = [];

    public bool HasDetailMachineStatuses => DetailMachineStatuses.Count > 0;

    // Same rebuild-and-clear rule as the actions (ADR 0559): a status inherited from the previous subject
    // is a claim about the wrong document.
    internal void SetDetailMachineStatuses(IReadOnlyList<DocumentsClient.MachineStatusInfo>? statuses)
    {
        DetailMachineStatuses.Clear();
        foreach (var status in statuses ?? [])
        {
            DetailMachineStatuses.Add(new MachineStatusRow(
                SimplArchive.Presentation.MachineStatusDisplay.Pretty(status.Name), status.Satisfied, status.Failures));
        }

        OnPropertyChanged(nameof(HasDetailMachineStatuses));
    }

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
            var contentChanged = false;
            foreach (var action in autoRefresh)
            {
                contentChanged |= await _api.Documents.ExecuteActionAsync(action);
            }

            if (!contentChanged)
            {
                return; // "current" throughout — the contents on screen are already the truth (ADR 0814)
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
            var ran = await _api.Documents.ExecuteActionAsync(action);
            Status = action.Label;

            // An auto-refresh action (ADR 0756) staged fresh content under the OPEN FOLDER — reload its
            // contents so it appears, the same as the on-open path; when the hook answered "current"
            // (ADR 0814) nothing was staged and there is nothing to re-read.
            if (IsAutoRefresh(action.Rel))
            {
                if (ran && _currentFolderId is { } openFolder)
                {
                    await LoadFolderContentsAsync(openFolder, _currentFolderLinks);
                }

                return;
            }

            // The action changed the subject's state, so its rels — including this surface — are stale;
            // re-reading the resource is what makes a state transition's NEW actions appear (ADR 0550).
            if (_detailLinks is { } links && links.Href("self") is { } selfHref)
            {
                var detail = await _api.Documents.GetDocumentDetailAsync(selfHref);
                SetDetailGenericActions(detail.GenericActions);
                SetDetailMachineStatuses(detail.MachineStatuses);
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
        (_detailLinks?.Rels ?? [])
            .Where(IsAutoRefresh)
            .Select(rel => new DocumentsClient.GenericActionInfo(rel, string.Empty, "POST", _detailLinks!.Href(rel)!))
            .ToList();

    /// <summary>
    /// The populate hook follows every interaction (ADR 0764): SELECTING a subject whose resource carries a
    /// machine-auto-refresh rel — the weather folder, or a leaf carrying its parent's — re-fetches, then
    /// re-reads what is on screen. CanSee is enough (the update is automated; the module principal writes).
    /// The cooldown lives in the HOOK since ADR 0814 (#1309): the server answers "current" without any
    /// upstream fetch while the folder holds unexpired staged content, and that answer — surfaced as
    /// <c>ExecuteActionAsync</c> returning false — is also what stops the reload below from re-triggering
    /// this hook forever. The 30-second per-href timestamps kept here before were one of two client-side
    /// copies of a policy that belonged to the hook.
    /// </summary>
    private async Task AutoRefreshSelectionAsync(NodeViewModel document)
    {
        if (_api is null)
        {
            return;
        }

        var actions = AutoRefreshActions();
        if (actions.Count == 0)
        {
            return;
        }

        var contentChanged = false;
        try
        {
            foreach (var action in actions)
            {
                contentChanged |= await _api.Documents.ExecuteActionAsync(action);
            }
        }
        catch (ApiActionException)
        {
            return; // a refused populate is not worth interrupting a selection
        }

        if (!contentChanged)
        {
            return; // "current" throughout — nothing was replaced, so there is nothing to re-read (ADR 0814)
        }

        if (_selectedDocumentId != document.Id)
        {
            return; // the user moved on (ADR 0559)
        }

        // The content WAS replaced (the server said "ran"): re-read what is on screen — the recursive
        // load's own hook answers "current" now, so it ends here rather than looping (ADR 0814) — and the
        // listed children too when the subject IS the open folder.
        if (_currentFolderId is { } open && open == document.Id)
        {
            await LoadFolderContentsAsync(open, _currentFolderLinks);
        }

        await LoadDetailAsync(document);
    }
}
