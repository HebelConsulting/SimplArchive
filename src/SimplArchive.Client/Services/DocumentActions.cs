using System.Net;
using System.Net.Http.Json;
using MudBlazor;
using SimplArchive.Client.Dialogs;
using SimplArchive.Client.Models;
using SimplArchive.Localization;

namespace SimplArchive.Client.Services;

/// <summary>The actions a document row offers, wherever that row is shown.</summary>
/// <remarks>
/// <para>
/// Seven of them — rename, move, delete, place reference, show references, legal hold, manage access — are
/// offered by BOTH the tree pane and the contents-list rows. Extracting either pane first would have stranded
/// them in the shell behind fourteen callbacks, so they come out first and both panes then call the same
/// implementation (ADR 0558).
/// </para>
/// <para>
/// What is NOT shared is the menu itself: the tree offers new-folder/upload/sort/subscribe/export, the list
/// offers check-out/versions/ancestors/reference-target, and their guards differ. A single menu component would
/// be two menus behind a mode flag — N copies wearing a hat — so each pane keeps its own markup and only the
/// behaviour is shared.
/// </para>
/// <para>
/// Each action reports <c>true</c> when something actually changed, and refreshing is left to the caller. That
/// is deliberate: the tree and the contents list refresh different things, and a service that refreshed for
/// them would have to know which pane called it. Deleting the currently-detailed document also has to clear the
/// detail pane — again the caller's business, not this one's.
/// </para>
/// </remarks>
public sealed class DocumentActions(HttpClient http, IDialogService dialogs, ISnackbar snackbar, ApiRoot apiRoot)
{
    /// <summary>
    /// A row's own address, taken from the rel it advertises rather than built from its id (ADR 0543). A row
    /// that advertises neither is not actionable, which is what <c>null</c> means to every caller here.
    /// </summary>
    public static string? DocumentAddress(BrowseNode node) =>
        node.Links?.GetValueOrDefault("document") ?? node.Links?.GetValueOrDefault("self");

    /// <summary>Opens the manage-access dialog for a row.</summary>
    public Task OpenManageAccessAsync(BrowseNode node) =>
        OpenManageAccessAsync(node.Id, node.Name, DocumentAddress(node));

    /// <summary>Opens the manage-access dialog for a document identified some other way (the detail pane).</summary>
    public async Task OpenManageAccessAsync(Guid documentId, string name, string? documentHref)
    {
        var parameters = new DialogParameters { ["DocumentId"] = documentId, ["DocumentName"] = name, ["DocumentHref"] = documentHref };
        await dialogs.ShowAsync<ManageAccessDialog>(Strings.Get("MaManageAccess"), parameters, new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Medium, FullWidth = true });
    }

    public async Task<bool> RenameAsync(BrowseNode node)
    {
        var parameters = new DialogParameters<RenameDialog> { { x => x.CurrentName, node.Name } };
        var dialog = await dialogs.ShowAsync<RenameDialog>(Strings.Get("RibbonRename"), parameters);
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: string newName } || newName == node.Name)
        {
            return false;
        }

        if (DocumentAddress(node) is not { } selfHref)
        {
            return false;
        }

        var etag = await GetETagAsync(selfHref);
        using var request = new HttpRequestMessage(HttpMethod.Put, selfHref)
        {
            Content = JsonContent.Create(new { name = newName }),
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            snackbar.Add(string.Format(Strings.Get("StNameTaken"), newName), Severity.Warning);
            return false;
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            snackbar.Add(Strings.Get("StNoPermRename"), Severity.Warning);
            return false;
        }
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            snackbar.Add(Strings.Get("StStaleReload"), Severity.Warning);
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            snackbar.Add(Strings.Get("StErrRenameItem"), Severity.Error);
            return false;
        }

        snackbar.Add(string.Format(Strings.Get("StRenamedTo"), newName), Severity.Success);
        return true;
    }

    public async Task<bool> MoveAsync(BrowseNode node)
    {
        if (await PickFolderAsync("Move to folder") is not { } folderId)
        {
            return false;
        }

        if (DocumentAddress(node) is not { } selfHref)
        {
            return false;
        }

        // The ETag probe follows the row's address; the `move` rel that would replace the path below lives on
        // the full document resource, not on a listing row, so converting it needs a fetch first (issue #416).
        var etag = await GetETagAsync(selfHref);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/documents/{node.Id}/parent")
        {
            Content = JsonContent.Create(new { parentId = folderId }),
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        var response = await http.SendAsync(request);
        if (!await HandleMutationAsync(response, $"Moved '{node.Name}'.", node.IsFolder
            ? "Can't move a folder into itself or one of its own sub-folders."
            : "Can't move the item there.", "An item with that name already exists in the target folder."))
        {
            return false;
        }

        return true;
    }

    public async Task<bool> DeleteAsync(BrowseNode node)
    {
        var message = node.IsFolder
            ? $"Delete the folder '{node.Name}' and everything inside it? It will be moved to the recycle bin."
            : $"Delete '{node.Name}'? It will be moved to the recycle bin.";
        var confirmed = await dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Delete",
            Message = message,
            YesText = "Delete",
            CancelText = "Cancel",
        });
        if (confirmed != true)
        {
            return false;
        }

        if (DocumentAddress(node) is not { } selfHref)
        {
            return false;
        }

        var etag = await GetETagAsync(selfHref);
        using var request = new HttpRequestMessage(HttpMethod.Delete, selfHref);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            snackbar.Add(Strings.Get("StNoPermDelete"), Severity.Warning);
            return false;
        }
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            snackbar.Add(Strings.Get("StStaleReload"), Severity.Warning);
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            snackbar.Add(Strings.Get("StErrDeleteItem"), Severity.Error);
            return false;
        }

        snackbar.Add(string.Format(Strings.Get("StDeleted"), node.Name), Severity.Success);
        return true;
    }

    public async Task<bool> PlaceReferenceAsync(BrowseNode node)
    {
        if (await PickFolderAsync("Place reference in folder") is not { } folderId)
        {
            return false;
        }

        var response = await http.PostAsJsonAsync($"api/documents/{folderId}/references", new { targetId = node.Id });
        if (!await HandleMutationAsync(response, $"Placed a reference to '{node.Name}'.",
            "Can't reference an item into itself or one of its own sub-folders.", "This item is already referenced in that folder."))
        {
            return false;
        }

        return true;
    }

    public async Task<bool> PlaceLegalHoldAsync(BrowseNode node)
    {
        if (await LegalHoldPrompt.ShowAsync(dialogs, node.Name) is not { } result)
        {
            return false;
        }

        try
        {
            using var created = await http.PostAsJsonAsync(await apiRoot.RequireAsync("legalHolds"), new { name = result.Name, reason = result.Reason });
            created.EnsureSuccessStatusCode();
            var hold = await created.Content.ReadFromJsonAsync<LegalHoldDto>();
            using var added = await http.PostAsJsonAsync($"api/legal-holds/{hold!.Id}/items", new { documentId = node.Id });
            added.EnsureSuccessStatusCode();
            snackbar.Add(string.Format(Strings.Get("StPlacedUnderHold"), node.Name), Severity.Success);
            return true;
        }
        catch (Exception)
        {
            snackbar.Add(Strings.Get("StErrPlaceHold"), Severity.Error);
            return false;
        }
    }

    public async Task<ReferencesDialog.ReferencesResult?> ShowReferencesAsync(BrowseNode node)
    {
        var parameters = new DialogParameters<ReferencesDialog>
        {
            { x => x.ItemId, node.Id },
            { x => x.ItemName, node.Name },
            { x => x.ReferencingFoldersHref, node.Links?.GetValueOrDefault("referencing-folders") },
        };
        var dialog = await dialogs.ShowAsync<ReferencesDialog>(Strings.Get("RefDlgTitle"), parameters);
        var result = await dialog.Result;

        // Both outcomes are navigation or a primary-location change, and only the shell can do either — so the
        // choice is reported rather than acted on. Returning null means the dialog was dismissed.
        return result is { Canceled: false, Data: ReferencesDialog.ReferencesResult r } ? r : null;
    }

    /// <summary>Reads a resource's current ETag via HEAD, for the If-Match a mutation needs (ADR 0188).</summary>
    public async Task<string?> GetETagAsync(string selfHref)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, selfHref);
        var response = await http.SendAsync(request);
        return response.Headers.ETag?.Tag;
    }

    /// <summary>Asks the user to choose a target folder; null when they dismissed the picker.</summary>
    public async Task<Guid?> PickFolderAsync(string title)
    {
        var dialog = await dialogs.ShowAsync<FolderPickerDialog>(title);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: Guid folderId } ? folderId : null;
    }

    /// <summary>Turns a mutation response into a snackbar and a yes/no, so every action reports failure alike.</summary>
    public async Task<bool> HandleMutationAsync(HttpResponseMessage response, string success, string badRequest, string conflict)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.BadRequest:
                snackbar.Add(badRequest, Severity.Warning);
                return false;
            case HttpStatusCode.Forbidden:
                snackbar.Add(Strings.Get("StNoPermHere"), Severity.Warning);
                return false;
            case HttpStatusCode.Conflict:
                snackbar.Add(conflict, Severity.Warning);
                return false;
            case HttpStatusCode.PreconditionFailed:
                snackbar.Add(Strings.Get("StStaleReload"), Severity.Warning);
                return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            snackbar.Add(Strings.Get("StOperationFailed"), Severity.Error);
            return false;
        }

        snackbar.Add(success, Severity.Success);
        return true;
    }
}
