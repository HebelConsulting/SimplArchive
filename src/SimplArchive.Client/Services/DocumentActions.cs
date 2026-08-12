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

    // ---- Sharing, reminders and following (ADRs 0546 / "Document reminders" / "Document subscriptions") ----
    //
    // These arrived here from the workbench page (ADR 0558). They are the same kind of thing as the seven above
    // — something the user does to a document they picked — and they keep the same contract: no state is held,
    // every result is returned, and refreshing is the caller's business. That is what lets a stateless service
    // serve a chat-header bell, a ribbon button and a detail-pane icon without knowing any of them exist.

    /// <summary>
    /// Shares this document with someone who has no account (ADR 0546). The href is the document's own rel, so
    /// its absence means the document cannot be shared and the affordance is simply not offered.
    /// </summary>
    public Task OpenExternalLinksAsync(Guid documentId, string name, string linksHref) =>
        dialogs.ShowAsync<ExternalLinksDialog>(Strings.Get("ExtLinkTitle"), new DialogParameters
        {
            ["DocumentId"] = documentId,
            ["DocumentName"] = name,
            ["LinksHref"] = linksHref,
        });

    /// <summary>
    /// The cross-document "everything I have shared" list. Its address comes from the API ROOT rather than from
    /// a document, because it is not a property of whatever happens to be selected — and the rel's ABSENCE is
    /// meaningful: the feature is not available here, so the ribbon button is not drawn (ADR 0543/0546).
    /// </summary>
    public Task<string?> MyExternalLinksHrefAsync() => apiRoot.HrefAsync("externalLinks");

    /// <summary>
    /// Opens that list, returning the document the user asked to go to, if any. Navigating belongs to the
    /// workbench, which owns the tree and list panes; a dialog cannot see them.
    /// </summary>
    public async Task<(Guid DocumentId, Guid? ParentId)?> OpenMyExternalLinksAsync(string href)
    {
        // The directory the dialog's admin filters need, read HERE on a deliberate user action rather than
        // borrowed from the Users & groups tab's cache: that tab owns its own state (ADR 0558), and borrowing
        // meant the filters appeared only if the admin happened to have visited it first — order dependence
        // dressed up as a saved request. Empty is still fine; the server refuses the cross-user query regardless.
        var (users, groups) = await LoadPrincipalDirectoryAsync();

        // Wide enough for four data columns plus three row actions (issue #410). Without this the DIALOG is the
        // clipping box — the content can be as wide as it likes and Revoke still falls off its right edge.
        var dialog = await dialogs.ShowAsync<MyExternalLinksDialog>(
            Strings.Get("ExtLinkMyLinks"),
            new DialogParameters { ["LinksHref"] = href, ["Users"] = users, ["Groups"] = groups },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });

        return (await dialog.Result)?.Data is MyExternalLinksDialog.GoToDocument target
            ? (target.DocumentId, target.ParentId)
            : null;
    }

    /// <summary>Sets a reminder (Wiedervorlage) on this document (ADR "Document reminders").</summary>
    public Task OpenReminderAsync(Guid documentId, string name, string? remindersHref) =>
        dialogs.ShowAsync<ReminderDialog>(Strings.Get("RemTitle"), new DialogParameters
        {
            ["DocumentId"] = documentId,
            ["DocumentName"] = name,
            ["RemindersHref"] = remindersHref,
        });

    /// <summary>Whether the caller follows this document or folder. False when it cannot be determined.</summary>
    public async Task<bool> IsSubscribedAsync(string subscriptionHref)
    {
        try
        {
            return (await http.GetFromJsonAsync<SubscriptionResponse>(subscriptionHref))?.Subscribed ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Follows or unfollows, returning the new state — or <c>null</c> when it failed, so the caller leaves its
    /// own flag alone rather than showing a state the server never reached (ADRs "Document subscriptions" /
    /// "Folder / subtree subscriptions"; following a folder covers its whole subtree).
    /// </summary>
    public async Task<bool?> ToggleSubscriptionAsync(string subscriptionHref, bool subscribed, bool isFolder)
    {
        try
        {
            using var response = subscribed
                ? await http.DeleteAsync(subscriptionHref)
                : await http.PutAsync(subscriptionHref, null);
            response.EnsureSuccessStatusCode();

            var now = !subscribed;
            snackbar.Add(Strings.Get((isFolder, now) switch
            {
                (true, true) => "StFollowingFolder",
                (true, false) => "StUnfollowedFolder",
                (false, true) => "StFollowingDocument",
                (false, false) => "StUnfollowedDocument",
            }), Severity.Success);
            return now;
        }
        catch (Exception)
        {
            snackbar.Add(Strings.Get("StErrSubscription"), Severity.Error);
            return null;
        }
    }

    private async Task<(List<MyExternalLinksDialog.PrincipalOption> Users, List<MyExternalLinksDialog.PrincipalOption> Groups)> LoadPrincipalDirectoryAsync()
    {
        var users = new List<MyExternalLinksDialog.PrincipalOption>();
        var groups = new List<MyExternalLinksDialog.PrincipalOption>();

        try
        {
            var url = await apiRoot.RequireAsync("users");
            while (url is not null)
            {
                var page = await http.GetFromJsonAsync<UsersDirectoryResponse>(url);
                users.AddRange((page?.Users ?? []).Select(u => new MyExternalLinksDialog.PrincipalOption(u.Id, u.DisplayName, u.DisplayName)));
                url = Hypermedia.Links.Href(page?.Links, "next");
            }

            url = await apiRoot.RequireAsync("groups");
            while (url is not null)
            {
                var page = await http.GetFromJsonAsync<GroupsDirectoryResponse>(url);
                groups.AddRange((page?.Groups ?? []).Select(g => new MyExternalLinksDialog.PrincipalOption(g.Id, g.Name, g.Name)));
                url = Hypermedia.Links.Href(page?.Links, "next");
            }
        }
        catch (Exception)
        {
            // Non-fatal: a caller who cannot read the directory simply gets no filters, which is what a
            // non-admin sees anyway.
        }

        return (users, groups);
    }

    private sealed record SubscriptionResponse
    {
        public bool Subscribed { get; set; }
    }

    private sealed record UsersDirectoryResponse
    {
        public List<UserDirectoryRow> Users { get; set; } = [];

        public List<Hypermedia.LinkResponse> Links { get; set; } = [];
    }

    private sealed record UserDirectoryRow
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";
    }

    private sealed record GroupsDirectoryResponse
    {
        public List<GroupDirectoryRow> Groups { get; set; } = [];

        public List<Hypermedia.LinkResponse> Links { get; set; } = [];
    }

    private sealed record GroupDirectoryRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }
}
