using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using SimplArchive.Client.Services;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// The detail pane's ONE edit mode (ADR 0278): read-only until Edit, then name, document date, OCR languages,
// mask and index data all become editable at once; one Save persists what changed, Esc cancels (ADR 0550).
//
// The lifecycle itself belongs to DetailEditor (ADR 0558). What is here is the SHELL's half: reporting the
// outcome, and the work a save implies that the editor cannot reach -- a renamed document changes rows in the
// tree and in the contents listing.
//
// Named to match the desktop's MainWindowViewModel.DetailEdit.cs, which holds the same subject. ADR 0511 asks
// that a web/desktop pair be reviewed as a single surface, and that is only possible when both halves can be
// found under the same name.
//
// The heading these came from covered three further methods -- the versions dialog, the compare-versions
// dialog and a workflow transition -- which are pane ACTIONS rather than its edit mode, so they stayed behind
// under a heading that now says so (#941).
public partial class Home
{
    // The lifecycle itself is DetailEditor's (ADR 0558); what stays here is the reporting and the shell work a
    // save implies — a renamed document changes rows in the tree and the listing, which the editor cannot reach.
    private async Task BeginEditAsync()
    {
        try
        {
            await Editor.BeginAsync();
        }
        catch (HttpRequestException e)
        {
            Snackbar.Add(string.Format(Strings.Get("StErrStartEdit"), e.Message), Severity.Error);
        }
    }

    // Esc while editing (ADR 0550); ignored otherwise, so the pane keeps its ordinary behaviour. Synchronous and
    // WITHOUT preventDefault: Escape has no default worth suppressing, and suppressing keys wholesale is what
    // broke text entry in this pane.
    private void OnDetailKeyDown(KeyboardEventArgs e)
    {
        if (Detail.IsEditing && e.Key == "Escape")
        {
            Editor.Cancel();
        }
    }

    // The editor persists the fields; the page reports the outcome and does the work a save implies elsewhere —
    // a renamed document changes rows in the tree and the open listing, which the editor cannot reach (ADR 0558).
    private async Task SaveDetailAsync()
    {
        if (Detail.Node is not { } item)
        {
            return;
        }

        var outcome = await Editor.SaveAsync();

        // A duplicate address claim is a QUESTION, not a refusal (#703): the message names the mailbox
        // already claiming the address, and confirming makes delivery fan out to both. On yes, the save
        // re-runs with the confirmation — fields already committed are skipped by their change detection.
        if (outcome.DuplicateClaim is { } claim)
        {
            var confirmed = await DialogService.ShowMessageBoxAsync(new MessageBoxOptions
            {
                Title = Strings.Get("DupClaimTitle"),
                Message = claim,
                YesText = Strings.Get("DupClaimConfirm"),
                CancelText = Strings.Get("Cancel"),
            });
            if (confirmed != true)
            {
                return; // the edit stays open, the list unchanged
            }

            outcome = await Editor.SaveAsync(confirmDuplicateClaims: true);
        }

        if (!outcome.Saved)
        {
            // Named, not spelled: the editor says WHICH fields were refused and the page says it in the reader's
            // language — the labels used to be English literals inside a localized sentence.
            var named = outcome.Failures.Select(f => Strings.Get(SaveFailureKey(f)));
            Snackbar.Add(string.Format(Strings.Get("StErrSaveJoin"), string.Join("; ", named)), Severity.Warning);
            return; // the edit stays open so the rejected field can be fixed
        }

        Snackbar.Add(Strings.Get("StSaved"), Severity.Success);

        // The OPEN folder's own listing re-sorts only when it is the folder whose order changed.
        if (outcome.ContentsSortOrderChanged && _selectedFolder?.Id == item.Id)
        {
            _folderSortOrder = Detail.SortOrder;
            _listPane?.ResetHeaderSort(); // re-apply the persisted default order to the current listing
        }

        if (outcome.NameChanged)
        {
            // Rebuild the tree/list so rows show the new name, then reselect the *fresh* node (the old
            // _selectedItem still carries the old name, which would otherwise reappear in the detail pane).
            await ReloadTreeAsync();
            if (_selectedFolder is { } folder)
            {
                _folderContents = await OpenFolderContentsAsync(folder);
            }

            var fresh = (_folderContents ?? []).FirstOrDefault(c => c.Id == item.Id) ?? item with { Name = Detail.SysName };
            await SelectItemAsync(fresh);
        }
        else
        {
            await SelectItemAsync(item);
        }
    }

    // The two sort-order refusals reuse the keys the folder-order picker already had; the rest are new, because
    // until now they were not translated at all.
    private static string SaveFailureKey(DetailSaveFailure failure) => failure switch
    {
        DetailSaveFailure.NameConflict => "SaveFailNameConflict",
        DetailSaveFailure.DocumentDate => "SaveFailDocumentDate",
        DetailSaveFailure.OcrLanguages => "SaveFailOcrLanguages",
        DetailSaveFailure.Sensitivity => "SaveFailSensitivity",
        DetailSaveFailure.Tags => "SaveFailTags",
        DetailSaveFailure.MaskAndIndexData => "SaveFailMaskIndexData",
        DetailSaveFailure.ContentsSortOrder => "FolderSortSaveFailed",
        DetailSaveFailure.ContentsSortOrderForbidden => "FolderSortNoPermission",
        _ => "SaveFailName",
    };
}
