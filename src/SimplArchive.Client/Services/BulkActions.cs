using System.Net.Http.Json;
using MudBlazor;
using SimplArchive.Client.Dialogs;
using SimplArchive.Localization;

namespace SimplArchive.Client.Services;

/// <summary>
/// The actions that operate on a MULTI-SELECTION (ADR "Bulk actions on selected documents") — move, delete, tag,
/// classify — plus the shared runner they and the drag-drop handler both post through.
/// </summary>
/// <remarks>
/// A sibling of <see cref="DocumentActions"/>, and split from it on the same line the UI draws: those act on one
/// row and are offered by its context menu; these act on the set and are offered by the bulk bar. Each returns
/// whether the caller should refresh, so the page keeps ownership of the selection and of what "refresh" means.
/// </remarks>
public sealed class BulkActions(HttpClient http, IDialogService dialogs, ISnackbar snackbar, DocumentActions actions)
{
    /// <summary>Move the selected items into a folder the user picks. False if they cancelled.</summary>
    public async Task<bool> MoveAsync(IReadOnlyCollection<Guid> ids)
    {
        if (await actions.PickFolderAsync("Move selected items to folder") is not { } folderId)
        {
            return false;
        }

        return await RunAsync("/api/documents/bulk/move", new { ids = ids.ToList(), parentId = folderId }, "moved");
    }

    /// <summary>Send the selected items to the recycle bin, after confirmation.</summary>
    public async Task<bool> DeleteAsync(IReadOnlyCollection<Guid> ids)
    {
        var confirmed = await dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Delete selected items",
            Message = $"Move {ids.Count} item(s) to the recycle bin?",
            YesText = "Delete",
            CancelText = "Cancel",
        });
        if (confirmed != true)
        {
            return false;
        }

        return await RunAsync("/api/documents/bulk/delete", new { ids = ids.ToList() }, "deleted");
    }

    /// <summary>Add tags, chosen in a dialog, to every selected item.</summary>
    public async Task<bool> AddTagsAsync(IReadOnlyCollection<Guid> ids)
    {
        var dialog = await dialogs.ShowAsync<BulkTagsDialog>(Strings.Get("BulkTagsConfirm"));
        return (await dialog.Result) is { Canceled: false, Data: List<string> tags } && tags.Count > 0
            && await RunAsync("/api/documents/bulk/tags", new { ids = ids.ToList(), tags }, "tagged");
    }

    /// <summary>Apply one sensitivity label to every selected item.</summary>
    public async Task<bool> SetSensitivityAsync(IReadOnlyCollection<Guid> ids)
    {
        var dialog = await dialogs.ShowAsync<BulkSensitivityDialog>(Strings.Get("BulkSensTitle"));
        return (await dialog.Result) is { Canceled: false, Data: int label }
            && await RunAsync("/api/documents/bulk/sensitivity", new { ids = ids.ToList(), label }, "classified");
    }

    /// <summary>
    /// Posts one bulk request and reports what it did. Public because the drag-drop handler posts through it too
    /// (a drop onto a folder is a bulk move or a bulk reference), and duplicating the partial-success reporting
    /// is exactly the kind of copy that drifts.
    /// </summary>
    /// <returns>True when the request succeeded and the caller should refresh.</returns>
    public async Task<bool> RunAsync(string url, object body, string verb)
    {
        try
        {
            var response = await http.PostAsJsonAsync(url, body);
            if (!response.IsSuccessStatusCode)
            {
                snackbar.Add(string.Format(Strings.Get("StBulkFailedStatus"), (int)response.StatusCode), Severity.Error);
                return false;
            }

            // A bulk endpoint reports per-item outcomes rather than failing wholesale: the server skips what the
            // caller may not touch (a legal hold, a cycle, a duplicate name) and says how many. A skip is a
            // WARNING, not an error — the request did what it was allowed to do.
            var result = await response.Content.ReadFromJsonAsync<BulkResultDto>();
            var msg = $"{result?.Succeeded ?? 0} item(s) {verb}" + (result?.Skipped > 0 ? $", {result.Skipped} skipped" : ".");
            snackbar.Add(msg, result?.Skipped > 0 ? Severity.Warning : Severity.Success);
            return true;
        }
        catch (Exception e)
        {
            snackbar.Add(string.Format(Strings.Get("StBulkFailed"), e.Message), Severity.Error);
            return false;
        }
    }

    private record BulkResultDto
    {
        public int Succeeded { get; set; }
        public int Skipped { get; set; }
    }
}
