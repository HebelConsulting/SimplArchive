using System.Net.Http.Json;
using MudBlazor;
using SimplArchive.Client.Hypermedia;
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
public sealed class BulkActions(HttpClient http, IDialogService dialogs, ISnackbar snackbar, DocumentActions actions, ApiRoot apiRoot)
{
    private IReadOnlyDictionary<string, string>? _rels;

    /// <summary>
    /// The bulk collection's own actions, read once from the address the API root advertises.
    /// </summary>
    /// <remarks>
    /// Cached deliberately, and ADR 0557 says when that is allowed: a rel set that is STRUCTURALLY FIXED may be
    /// held, content never may. These five are the collection's operations, not its contents — they do not vary
    /// by document, by user or by page — so one read serves the session and every bulk action is a follow.
    ///
    /// The alternative shapes are both wrong. Appending `/move` to the collection's href is composing in
    /// disguise (the client would be asserting the API's path structure, ADR 0557); re-reading the collection
    /// per action is a request spent re-learning something fixed.
    /// </remarks>
    private async Task<string> HrefAsync(string rel)
    {
        _rels ??= (await http.GetFromJsonAsync<BulkCollectionDto>(await apiRoot.RequireAsync("documentsBulk")))?.RelMap()
            ?? throw new InvalidOperationException("The bulk collection advertised no actions (ADR 0543).");
        return _rels.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The bulk collection advertised no '{rel}' rel (ADR 0543).");
    }

    /// <summary>Move the selected items into a folder the user picks. False if they cancelled.</summary>
    public async Task<bool> MoveAsync(IReadOnlyCollection<Guid> ids)
    {
        if (await actions.PickFolderAsync("Move selected items to folder") is not { } folderId)
        {
            return false;
        }

        return await RunAsync(await HrefAsync("move"), new { ids = ids.ToList(), parentId = folderId }, "moved");
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

        return await RunAsync(await HrefAsync("delete"), new { ids = ids.ToList() }, "deleted");
    }

    /// <summary>Add tags, chosen in a dialog, to every selected item.</summary>
    public async Task<bool> AddTagsAsync(IReadOnlyCollection<Guid> ids)
    {
        var dialog = await dialogs.ShowAsync<BulkTagsDialog>(Strings.Get("BulkTagsConfirm"));
        return (await dialog.Result) is { Canceled: false, Data: List<string> tags } && tags.Count > 0
            && await RunAsync(await HrefAsync("tags"), new { ids = ids.ToList(), tags }, "tagged");
    }

    /// <summary>Apply one sensitivity label to every selected item.</summary>
    public async Task<bool> SetSensitivityAsync(IReadOnlyCollection<Guid> ids)
    {
        var dialog = await dialogs.ShowAsync<BulkSensitivityDialog>(Strings.Get("BulkSensTitle"));
        return (await dialog.Result) is { Canceled: false, Data: int label }
            && await RunAsync(await HrefAsync("sensitivity"), new { ids = ids.ToList(), label }, "classified");
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

    /// <summary>
    /// Follows one of the collection's advertised actions by NAME — for the drag-drop handler, which posts a
    /// move or a reference without going through the bulk bar.
    /// </summary>
    public async Task<bool> RunRelAsync(string rel, object body, string verb) =>
        await RunAsync(await HrefAsync(rel), body, verb);

    private sealed record BulkCollectionDto
    {
        public List<LinkResponse> Links { get; set; } = [];

        public IReadOnlyDictionary<string, string>? RelMap() => Hypermedia.Links.RelMap(Links);
    }

    private record BulkResultDto
    {
        public int Succeeded { get; set; }
        public int Skipped { get; set; }
    }
}
