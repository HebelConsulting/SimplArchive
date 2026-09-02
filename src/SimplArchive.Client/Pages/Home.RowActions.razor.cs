using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using SimplArchive.Client.Dialogs;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;
using SimplArchive.Client.Services;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// What ONE row does: delete it, show what references it, label it, copy a deep link to it, go to it, drop it on
// a folder as a move or a reference, promote a reference to be the primary location -- plus creating a new
// repository, which is the same shape one level up. The shell's half of the shared row actions (ADR 0558): each
// reports whether anything changed and the shell decides what to refresh, which is precisely what differs
// between the tree and the contents list.
//
// Deliberately NOT the bulk actions, which act on the multi-selection and keep their own section and service.
// The distinction is the whole reason this file exists: four single-row actions had drifted under the heading
// "Bulk actions on the multi-selection", so that heading described eleven members of which seven were bulk.
//
// The heading these members belong under already existed -- "Move / reference (row action menu)" -- and was
// EMPTY: three lines, no members, immediately followed by the bulk heading that had absorbed them. A heading
// outliving its contents is issue #941's usual shape; a heading whose contents migrated to the NEXT heading,
// leaving the original standing empty, is the same decay caught in the act.
//
// A partial of Home rather than a component, by ADR 0733's test: these bring no markup. They are invoked from
// the contents list's row menu, the tree's context menu and the ribbon -- three surfaces that stay in the shell
// -- so a child component would need the selection, the refresh, the tree reload and the snackbar handed down.
public partial class Home
{
    // The shell's half of the shared row actions (ADR 0558): each reports whether anything changed, and the
    // shell decides what to refresh — which is precisely what differs between the tree and the contents list.
    private async Task RunAndRefreshAsync(Task<bool> action)
    {
        if (await action)
        {
            await RefreshAsync();
        }
    }

    // Deleting the row the detail pane is showing must also empty that pane — the pane is the shell's.
    private async Task DeleteRowAsync(BrowseNode node)
    {
        if (!await Actions.DeleteAsync(node))
        {
            return;
        }

        if (_selectedItem?.Id == node.Id)
        {
            ClearDetail();
        }

        await RefreshAsync();
    }

    // The references dialog ends in navigation or a primary-location change; both are the shell's to perform,
    // so the service reports the choice and this acts on it.
    private async Task ShowReferencesForAsync(BrowseNode node)
    {
        if (await Actions.ShowReferencesAsync(node) is not { } r)
        {
            return;
        }

        if (r.Promote)
        {
            await PromotePrimaryLocationAsync(node, r.FolderId);
        }
        else
        {
            // Open the chosen folder AND select the item — its real row in the primary location, or its
            // reference (shortcut) row in a referencing folder.
            await NavigateToFolderAsync(r.FolderId, node.Id);
        }
    }

    // Offered by the Tenant tab's header, but they act on state the SHELL owns — the repository tree, and
    // the detail pane's sensitivity picker — so they stay here and reach the tab as callbacks (ADR 0558).
    // Manage the tenant's configurable sensitivity labels (ADR "Configurable sensitivity labels + upload
    // defaults"); reload the picker list on close so a detail-pane edit sees any changes.
    private async Task OpenSensitivityLabelsAsync()
    {
        var dialog = await DialogService.ShowAsync<SensitivityLabelsDialog>(Strings.Get("SlTitle"), new DialogOptions { MaxWidth = MaxWidth.Medium });
        await dialog.Result;
        await Catalogs.ReloadSensitivityAsync();
    }

    private async Task NewRepositoryAsync()
    {
        var input = await JS.InvokeAsync<string?>("prompt", "New repository name:");
        if (string.IsNullOrWhiteSpace(input)) return;
        var resp = await Http.PostAsJsonAsync(await ApiRoot.RequireAsync("repositories"), new { name = input.Trim() });
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict) { Snackbar.Add(Strings.Get("StRepoNameTaken"), Severity.Warning); return; }
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) { Snackbar.Add(Strings.Get("StNoPermCreateRepo"), Severity.Error); return; }
        if (!resp.IsSuccessStatusCode) { Snackbar.Add(Strings.Get("StErrCreateRepo"), Severity.Error); return; }
        Snackbar.Add(string.Format(Strings.Get("StCreatedRepo"), input.Trim()), Severity.Success);
        await ReloadTreeAsync();
    }

    // An internal node drag (a list row or a tree folder) was dropped on a folder — move or reference the dragged
    // set into it (ADR "Desktop drag-and-drop move and reference", web parity). Called from dropUpload.js. The set
    // is the whole multi-selection when the grabbed row belongs to a ≥2 selection, else just the grabbed node; an
    // all-shortcuts drag only ever places references, otherwise a Move/Reference prompt is shown. The repo-root
    // gate and cycle/duplicate skips live server-side in the bulk endpoints.
    [JSInvokable]
    public async Task PerformNodeDropAsync(string targetFolderId, string draggedNodeId, bool draggedIsRef)
    {
        if (!Guid.TryParse(targetFolderId, out var targetId) || !Guid.TryParse(draggedNodeId, out var draggedId))
        {
            return;
        }

        var draggedInBulk = _bulkIds.Count >= 2 && _bulkIds.Contains(draggedId);
        var ids = (draggedInBulk ? _bulkIds.ToList() : [draggedId])
            .Where(id => id != targetId)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        bool IsRef(Guid id) => _folderContents.FirstOrDefault(n => n.Id == id)?.IsReference ?? false;
        var allRefs = draggedInBulk ? ids.All(IsRef) : draggedIsRef;
        if (allRefs)
        {
            await RunBulkAsync(Bulk.RunRelAsync("reference", new { ids, parentId = targetId }, "referenced"));
            return;
        }

        var them = ids.Count == 1 ? "it" : "them";
        var where = ids.Count == 1 ? "it is" : "they are";
        var label = ids.Count == 1 ? "this item" : $"{ids.Count} items";
        var choice = await DialogService.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Move or reference",
            Message = $"Move {label} here, or place a reference (shortcut) that leaves {them} where {where}?",
            YesText = "Move",
            NoText = "Reference",
            CancelText = "Cancel",
        });

        switch (choice)
        {
            case true:
                await RunBulkAsync(Bulk.RunRelAsync("move", new { ids, parentId = targetId }, "moved"));
                break;
            case false:
                await RunBulkAsync(Bulk.RunRelAsync("reference", new { ids, parentId = targetId }, "referenced"));
                break;
        }

        // A [JSInvokable] call doesn't auto-render the component (unlike a Blazor event handler), so refresh the UI
        // explicitly after RunBulkAsync updated the folder listing — same pattern as OnUploadsCompleteAsync.
        StateHasChanged();
    }


    private async Task GoToAsync(BrowseNode node)
    {
        if (node.RealParentId is { } parentId)
        {
            await NavigateToFolderAsync(parentId, node.Id);
        }
        else
        {
            await NavigateToFolderAsync(node.Id);
        }
    }

    private async Task RemoveReferenceAsync(BrowseNode node)
    {
        if (_selectedFolder is not { } folder)
        {
            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Remove reference",
            Message = $"Remove the reference to '{node.Name}' from this folder? The item itself is not deleted.",
            YesText = "Remove",
            CancelText = "Cancel",
        });
        if (confirmed != true)
        {
            return;
        }

        // The reference row advertises its own removal address — the pair (folder, reference) is exactly what it
        // stands for, so nothing here needs rebuilding (issue #416).
        if (node.Links?.GetValueOrDefault("delete") is not { } removeReferenceHref)
        {
            return;
        }

        var response = await Http.DeleteAsync(removeReferenceHref);
        if (!response.IsSuccessStatusCode)
        {
            Snackbar.Add(Strings.Get("StErrRemoveReference"), Severity.Error);
            return;
        }

        Snackbar.Add(string.Format(Strings.Get("StRemovedRef"), node.Name), Severity.Success);
        await RefreshAsync();
    }


    // Promote a referenced folder to be the document's primary location (ADR 0506) — the action itself is a
    // row action and lives in DocumentActions; the page follows the document to its new home afterwards.
    private async Task PromotePrimaryLocationAsync(BrowseNode node, Guid folderId)
    {
        if (await Actions.SetPrimaryLocationAsync(node, folderId))
        {
            await RefreshAsync();
            await NavigateToFolderAsync(folderId, node.Id);
        }
    }
}
