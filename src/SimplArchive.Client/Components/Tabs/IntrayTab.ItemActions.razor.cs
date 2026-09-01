using System.Net.Http.Json;
using Microsoft.JSInterop;
using MudBlazor;
using SimplArchive.Localization;

namespace SimplArchive.Client.Components.Tabs;

/// <summary>
/// <see cref="IntrayTab"/>'s per-item actions: download, send to another queue, move, and delete.
/// </summary>
/// <remarks>
/// <para>
/// A partial of the SAME component rather than a component of its own, and that is the decision worth
/// recording. These are invoked from the per-row ⋮ menu, and they read and write the tab's selection
/// (<c>_selectedIntrayItem</c>) and its preview pane. As a child component they would have needed the
/// selection, the preview-clear, the status line and the reload passed down as four or five callbacks — a
/// callback bag, which is a worse shape than the one it replaced, and (per the page-operations component
/// beside this one) the shape in which a Blazor child quietly stops re-rendering.
/// </para>
/// <para>
/// Splitting by responsibility into a partial keeps the extraction honest — this file is one subject, and
/// IntrayTab is smaller by exactly what moved — without inventing a seam the code does not have. It is the
/// same shape the desktop's MainWindow uses for its per-feature view glue.
/// </para>
/// </remarks>
public partial class IntrayTab
{
    private async Task DownloadIntrayItemAsync(IntrayItem item)
    {
        if (NotOffered(item.DownloadHref)) { return; }

        await JS.InvokeVoidAsync("open", item.DownloadHref, "_blank");
    }

    // Send an OWN item into another intray (ADR 0532): pick a group or a user, then move it there.
    private async Task SendIntrayItemAsync(IntrayItem item)
    {
        var parameters = new DialogParameters<Dialogs.IntraySendDialog> { { x => x.ItemName, item.Name } };
        var dialog = await DialogService.ShowAsync<Dialogs.IntraySendDialog>(Strings.Get("IntraySendTitle"), parameters);
        if ((await dialog.Result) is { Canceled: false, Data: Dialogs.IntraySendDialog.SendTarget t })
        {
            await MoveIntrayItemAsync(item, t.TargetGroupId, t.TargetUserId);
        }
    }

    // Move a NON-own item (a group's, or another user's for an admin) into my own intray to work it privately.
    private async Task MoveToMyIntrayAsync(IntrayItem item)
    {
        if (MyUserId is { } me)
        {
            await MoveIntrayItemAsync(item, null, me);
        }
    }

    private async Task MoveIntrayItemAsync(IntrayItem item, Guid? targetGroupId, Guid? targetUserId)
    {
        try
        {
            if (NotOffered(item.MoveHref)) { return; }

            var response = await Http.PostAsJsonAsync(item.MoveHref, new { targetGroupId, targetUserId });
            if (!response.IsSuccessStatusCode)
            {
                Snackbar.Add(string.Format(Strings.Get("StErrMoveItemStatus"), (int)response.StatusCode), Severity.Error);
                return;
            }

            Snackbar.Add(string.Format(Strings.Get("StMoved"), item.Name), Severity.Success);
            ClearIntraySelection(item);
            await LoadIntrayAsync();
        }
        catch (Exception)
        {
            Snackbar.Add(Strings.Get("StErrMoveItem"), Severity.Error);
        }
    }

    private async Task DeleteIntraySelectionAsync()
    {
        var items = _intrayChecked.Count > 0 ? _intrayChecked.ToList()
            : _selectedIntrayItem is { } one ? new List<IntrayItem> { one } : [];
        if (items.Count <= 1)
        {
            if (items.Count == 1)
            {
                await DeleteIntrayItemAsync(items[0]);
            }

            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Delete intray items",
            Message = string.Format(Strings.Get("IntrayDeleteManyConfirm"), items.Count),
            YesText = "Delete",
            CancelText = "Cancel",
        });
        if (confirmed != true)
        {
            return;
        }

        var deleted = 0;
        foreach (var item in items)
        {
            // SKIP rather than abandon the batch: the summary below branches on `deleted == items.Count` and
            // warns on a partial, which is the design saying a partial delete is an expected outcome (#933).
            if (NotOffered(item.DeleteHref)) { continue; }

            if ((await Http.DeleteAsync(item.DeleteHref)).IsSuccessStatusCode)
            {
                deleted++;
                ClearIntraySelection(item);
            }
        }

        Snackbar.Add(
            deleted == items.Count ? string.Format(Strings.Get("StDeletedItems"), deleted) : Strings.Get("StErrDeleteSelection"),
            deleted == items.Count ? Severity.Success : Severity.Warning);
        await LoadIntrayAsync();
    }

    private async Task DeleteIntrayItemAsync(IntrayItem item)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Delete intray item",
            Message = $"Delete '{item.Name}' from your intray?",
            YesText = "Delete",
            CancelText = "Cancel",
        });
        if (confirmed != true)
        {
            return;
        }

        // After a confirmation, which is the worst place for a silent failure: the user has said yes to
        // deleting a named item and would otherwise be told nothing at all (#863/#876).
        if (NotOffered(item.DeleteHref)) { return; }

        if ((await Http.DeleteAsync(item.DeleteHref)).IsSuccessStatusCode)
        {
            ClearIntraySelection(item);
            await LoadIntrayAsync();
        }
        else
        {
            Snackbar.Add(string.Format(Strings.Get("StErrDeleteNamed"), item.Name), Severity.Error);
        }
    }

    private void ClearIntraySelection(IntrayItem item)
    {
        if (_selectedIntrayItem == item)
        {
            _selectedIntrayItem = null;
            _preview?.Clear();
        }
    }
}
