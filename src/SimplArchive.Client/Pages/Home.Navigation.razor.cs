using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;
using SimplArchive.Client.Services;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// How the workbench shell responds to NAVIGATING: reloading the tree, opening a folder's contents, selecting a
// row or a folder, stepping into a browsed .zip, and loading (or clearing) the detail panes for whatever is now
// selected — plus the per-item affordances reached from those same panes.
//
// A partial of Home rather than a component, by ADR 0733's test: the markup these serve is the tree pane, the
// contents list and the detail header, none of which can come with them. As a child they would need the
// selection, the epoch counter, the detail model and half a dozen callbacks passed down — the callback bag that
// rule exists to refuse.
//
// The heading this arrived under said "Tree + list navigation" and covered four further subjects: archive
// browsing, external links, WebDAV and subscriptions. They travel together here because each is reached from
// the panes above and reads the same selection; splitting them further is a later tranche's job, not a reason
// to leave 484 lines in the shell.
public partial class Home
{

    // Rebuilds the tree's roots and re-renders the pane. The nodes live in the injected TreeState (they must
    // outlive the pane, which is disposed on every tab switch), so refreshing them is two steps: reload the
    // service, then tell the component — a change in a service is invisible to a component until it is.
    private async Task ReloadTreeAsync()
    {
        await Tree.ReloadAsync(_isTenantAdmin);
        _treePane?.Refresh();
    }

    // Reads a folder's contents for the CONTENTS PANE: the rows, plus the folder's persisted order adopted as
    // this listing's default (which also clears any ephemeral header sort — ADR "Per-folder contents sort
    // order"). A tree-child load wants the rows only, and calls the service directly.
    private async Task<List<BrowseNode>> OpenFolderContentsAsync(BrowseNode folder)
    {
        var contents = await Browse.LoadContentsAsync(folder.Id, folder.RepositoryId, BrowseService.ChildrenHrefOf(folder), BrowseService.ReferencesHrefOf(folder));
        _folderSortOrder = contents.SortOrder ?? _folderSortOrder;
        _listPane?.ResetHeaderSort();
        return contents.Nodes;
    }

    // Selecting a folder (tree click or drilling into a folder row) lists its contents in the middle pane.
    private async Task SelectFolderAsync(BrowseNode? folder)
    {
        _treeDrawerOpen = false; // phone: navigating a folder closes the tree drawer (no-op on desktop)
        // The synthetic Administration/Users nodes (ADR "Tenant-admin Administration → Users view") aren't real
        // folders — clicking them just expands; a user's personal repo node (a real repo id) browses normally.
        if (folder is { AdminKind: not "" })
        {
            return;
        }

        // The Intray / Check-out launcher nodes under Personal switch to their bottom tab (ADR "GUI-tree Personal
        // space grouping"); the personal-root node itself browses like any repository (falls through).
        if (folder is { PersonalKind: "intray" })
        {
            await SetTab(Tab.Intray);
            return;
        }

        if (folder is { PersonalKind: "checkout" })
        {
            await SetTab(Tab.Checkout);
            return;
        }

        _archiveDocument = null; // leave any archive-browsing view
        var openedAt = _selectionEpoch; // read before the first await (#784)
        var cameFrom = _selectedFolder?.Id;
        _selectedFolder = folder;
        _currentRepositoryId = folder?.RepositoryId ?? Guid.Empty;
        ClearDetail();
        // Opening ANOTHER folder ends the ephemeral header sort and the column filters (the pane's contract) —
        // the row-drill path has always done this via OpenFolderContentsAsync; the tree-click path silently
        // did not, so a sort or filter set in one folder kept narrowing the next one's rows.
        _listPane?.ResetHeaderSort();
        _folderContents = folder is null ? [] : (await Browse.LoadContentsAsync(folder.Id, folder.RepositoryId, BrowseService.ChildrenHrefOf(folder), BrowseService.ReferencesHrefOf(folder))).Nodes;

        // A row selected WHILE this load was in flight survives it.
        //
        // The load is asynchronous and the list stays clickable throughout — so a user can select a row, and
        // then have the arriving rows replace the list under them. The selection pointed at a row object that
        // is no longer in `_folderContents`, so it silently became nothing: the detail and preview panes
        // emptied and stayed empty, with no error and no way to tell why. It is the ADR 0559 window again,
        // seen from the other side — there the action outran the load, here the load outran the action.
        //
        // Re-pointed BY ID at the freshly-loaded row, because the row object is new even when the document is
        // the same. Gone from the folder (deleted, moved, filtered away) means the selection is genuinely
        // over, and the pane falls back to the folder below.
        //
        // FOLDER rows survive too (#811). First written for document rows only, and a folder row clicked
        // inside the window was silently reverted to the parent — 20 % of clicks at WAN latency, ~0 % on
        // localhost, which is why no local run ever saw it: the first aggressive load test did.
        if (_selectedNode is { } wasSelected
            && _folderContents.FirstOrDefault(n => n.Id == wasSelected.Id && n.IsReference == wasSelected.IsReference) is { } stillListed)
        {
            _selectedNode = stillListed;
            await LoadDetailForAsync(stillListed);
            if (stillListed.IsFolder)
            {
                await LoadFolderSubscriptionAsync(stillListed);
                await LoadCommentsAsync(stillListed);
            }
            return;
        }

        // A folder has its own comment thread, and — since issue #408 — its own detail pane: the same one a
        // document gets, because a folder is a Document with a mask and index data of its own.
        if (folder is not null)
        {
            // MOVING reveals, so the tree has a node to mark at all (ADR 0703) — a folder drilled into from the
            // list is a child of an unexpanded node, where nothing is loaded to mark. Without a parent first: a
            // node already in the tree is found directly, and naming a parent that is not one would expand an
            // unrelated node in front of the user.
            _scrollTreeCurrent = await Tree.RevealAsync(folder.Id) || await Tree.RevealAsync(folder.Id, cameFrom);

            // The rows have been on screen for a while, so the user may already have picked one — and this
            // tail's load would be NEWER than theirs, replacing their document with the folder. Measured: the
            // token alone left 3/60; this closed the rest (#784).
            if (_selectionEpoch != openedAt)
            {
                return;
            }

            await ShowFolderDetailAsync(folder);
        }
    }

    /// <summary>Describes the folder the user is standing in — the pane's subject with nothing selected.</summary>
    /// <remarks>Shared with the deselect path: "no selection" and "just opened" are one situation (ADR 0703).</remarks>
    private async Task ShowFolderDetailAsync(BrowseNode folder)
    {
        _selectedNode = folder;
        await LoadDetailForAsync(folder);
        await LoadFolderSubscriptionAsync(folder);
        await LoadCommentsAsync(folder);
    }

    /// <summary>Nothing is selected any more: the detail pane falls back to the open folder (ADR 0703).</summary>
    /// <remarks>Esc, or a click on the list's empty area. Before this a selection could be made but never unmade.</remarks>
    private async Task ClearListSelectionAsync()
    {
        if (Detail.IsEditing)
        {
            // Esc while editing means "cancel the edit" (ADR 0550), not "change the subject".
            return;
        }

        _bulkIds.Clear();
        _bulkAnchorId = null;
        ClearDetail();
        if (_selectedFolder is { } folder)
        {
            await ShowFolderDetailAsync(folder);
        }
    }

    // Single click only ever *selects* the row (never navigates), so e.g. a document's Download button
    // enables. A document loads its index-data/preview; a folder is selected (highlighted, its comments
    // shown) without opening — only a double click drills into it (OnRowDoubleClickAsync).
    // Native multi-selection (ADR "Bulk actions on selected documents") — no checkboxes: Ctrl/Cmd-click toggles
    // a row into the bulk set, Shift-click range-selects from the anchor, a plain click clears the set and
    // single-selects (the existing detail-pane behavior). When ≥2 rows are selected a bulk-action bar appears.
    private async Task OnRowClickAsync(BrowseNode node, Microsoft.AspNetCore.Components.Web.MouseEventArgs e)
    {
        if (e.CtrlKey || e.MetaKey)
        {
            if (!_bulkIds.Remove(node.Id))
            {
                _bulkIds.Add(node.Id);
                if (_bulkIds.Count == 1 && _bulkAnchorId is { } prior && prior != node.Id)
                {
                    _bulkIds.Add(prior); // fold the previously single-selected row into the multi-selection
                }
            }

            _bulkAnchorId = node.Id;
            StateHasChanged();
            return;
        }

        if (e.ShiftKey && _bulkAnchorId is { } anchorId)
        {
            var order = _folderContents.Select(n => n.Id).ToList();
            var a = order.IndexOf(anchorId);
            var b = order.IndexOf(node.Id);
            if (a >= 0 && b >= 0)
            {
                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                {
                    _bulkIds.Add(order[i]);
                }
            }

            StateHasChanged();
            return;
        }

        // Phone: a single tap navigates (there's no side-by-side detail) — a folder / doc-with-children / zip
        // drills in; a leaf document opens the full-screen detail overlay.
        if (_isSinglePane)
        {
            if (node.IsFolder || node.HasChildren || (node.HasVersions && node.FileExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                await OnRowDoubleClickAsync(node);
            }
            else
            {
                _phoneDetailTab = "preview";
                await SelectRowAsync(node);
            }
            return;
        }

        await SelectRowAsync(node);
    }

    // Plain single-select (clears any bulk multi-selection) — the row's default behavior, also used by
    // programmatic navigation (go-to / search result).
    private async Task SelectRowAsync(BrowseNode node)
    {
        _bulkIds.Clear();
        _bulkAnchorId = node.Id;
        if (node.HasVersions)
        {
            await SelectItemAsync(node);
        }
        else
        {
            await SelectContentFolderAsync(node);
        }
    }

    private readonly HashSet<Guid> _bulkIds = [];
    private Guid? _bulkAnchorId;

    // Double click drills into any node that has children — a folder, or a document that has child documents
    // (an email with filed attachments, ADR "Email attachments as child documents"), listing its contents.
    // On a childless document it's a no-op — the single click already selected it.
    private async Task OnRowDoubleClickAsync(BrowseNode node)
    {
        if (node.HasVersions && node.FileExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await EnterArchiveAsync(node);
        }
        else if (node.IsFolder || node.HasChildren)
        {
            await SelectFolderAsync(node);
        }
    }

    // Browse a .zip's entries virtually — read on demand from the stored archive, nothing unpacked (ADR
    // "Zip file browsing"). The parent folder listing (_folderContents) is left in place so Exit returns to it.
    private async Task EnterArchiveAsync(BrowseNode zip)
    {
        try
        {
            // The resource advertises archive-entries only for a zip (#416) — follow it rather than compose.
            var response = await Http.GetFromJsonAsync<ArchiveEntriesResponse>(await Browse.FetchRelAsync(zip.Id, "archive-entries"));
            _archiveEntries = (response?.Entries ?? [])
                .Select(e => new ArchiveEntryRow(e.Name, e.Path, e.Size,
                    e.Links?.FirstOrDefault(l => l.Rel == "download")?.Href?.TrimStart('/') ?? ""))
                .ToList();
            _archiveDocument = zip;
        }
        catch (Exception)
        {
            Snackbar.Add(string.Format(Strings.Get("StErrReadZip"), zip.Name), Severity.Error);
        }
    }

    private void ExitArchiveAsync()
    {
        _archiveDocument = null;
        _archiveEntries = [];
    }

    // Download one archive entry: the Api proxies the bytes (entries aren't storage objects), so fetch with
    // the authenticated client and hand the blob to the browser to save.
    private async Task DownloadArchiveEntryAsync(ArchiveEntryRow entry)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(entry.DownloadHref);
            using var stream = new MemoryStream(bytes);
            using var streamRef = new DotNetStreamReference(stream);
            await JS.InvokeVoidAsync("downloadFileFromStream", entry.Name, streamRef);
        }
        catch (Exception)
        {
            Snackbar.Add(string.Format(Strings.Get("StErrDownloadEntry"), entry.Name), Severity.Error);
        }
    }

    // Select a folder shown in the contents list without opening it: highlight it and show its comment
    // thread, but leave the current listing (_selectedFolder/_folderContents) in place, and clear any
    // document detail so the index/preview panes fall back to their placeholder.
    private async Task SelectContentFolderAsync(BrowseNode folder)
    {
        ClearDetail();
        _selectionEpoch++;
        _selectedNode = folder;
        // No reveal, and no mark. Selecting a row in the list does not move you, so the tree has nothing to
        // say about it — the ring stays on the folder you are standing in. Revealing on selection made the
        // tree answer two questions at once and gave the ring a meaning that changed with the row type.
        // (Reveal is still right where the user genuinely goes somewhere — "Go to" from a search hit.)
        //
        // The detail pane still follows the selection: a child folder's metadata is editable without
        // navigating into it (issue #408).
        await LoadDetailForAsync(folder);
        await LoadFolderSubscriptionAsync(folder);
        await LoadCommentsAsync(folder);
    }

    private async Task SelectItemAsync(BrowseNode item)
    {
        ClearDetail();
        _selectionEpoch++;
        _selectedItem = item;
        _selectedNode = item;
        await LoadDetailForAsync(item);
    }

    // Fills the detail pane for whatever it is describing — a document row, a folder row, or the open folder.
    // ONE loader for all three, because they now render one pane (issue #408). The READS live in DetailLoader
    // (ADR 0558); what stays here is only what is coupled to rendering, plus the token that says whether this
    // load is still the wanted one (#784).
    private async Task LoadDetailForAsync(BrowseNode item)
    {
        var token = DetailLoad.Begin();
        Detail.Node = item;

        // A drag of the detail pane lasts only until the SELECTION changes (ADR 0550) — which is now, and is
        // exactly when the fitted height would move anyway. Wrapped like every other interop here: a stale
        // cached module must degrade quietly rather than take the pane down with it (ADR 0500).
        if (_layoutModule is not null)
        {
            try { await _layoutModule.InvokeVoidAsync("resetIndexSizing"); }
            catch (JSException) { }
            catch (JSDisconnectedException) { }
        }

        if (DetailLoad.Superseded(token))
        {
            return;
        }

        var loaded = await DetailLoad.LoadAsync(item, token);
        if (loaded.Loaded)
        {
            _downloadUrl = loaded.DownloadUrl;

            // The pane may not EXIST yet: @ref is assigned after a render, and the two ways a document is
            // opened from elsewhere — a search hit, a notification — call SetTab(Repositories) and select in the
            // same synchronous run, with no render in between. Rendering straight into a null ref would lose the
            // preview silently on exactly those paths, so defer to OnAfterRenderAsync when it is not there yet.
            await ShowPreviewAsync(loaded.PreviewUrl, loaded.TextLayoutUrl, loaded.Converted, loaded.HasVersion);
        }

        await LoadCommentsAsync(item);
    }

    // The generic action surface's executor (ADR 0743): the labeled link's method against its advertised
    // href, no payload — the parameterless-transition shape the surface is scoped to. A refusal surfaces
    // the problem document's detail (since ADR 0742, the explanation a user can act on); success re-reads
    // the resource, because the action changed the subject's state and its rels — this surface included —
    // are stale (ADR 0550: a transition's NEW actions must appear).
    private async Task ExecuteGenericActionAsync(LinkResponse action)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(action.Method), action.Href.TrimStart('/'));
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // The errorCode, mapped through ApiErrorText — never the English `detail` (issue #424); an
                // unmapped module code falls back to its generic localized sentence until ADR 0742's engine
                // ships server-localized explanations as their own field.
                string? code = null;
                try
                {
                    var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                    code = problem.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
                }
                catch (System.Text.Json.JsonException) { }
                Snackbar.Add(SimplArchive.Localization.ApiErrorText.For(code), Severity.Error);
                return;
            }

            Snackbar.Add(action.Label ?? action.Rel, Severity.Success);
            if (Detail.Links is { } links && links.TryGetValue("self", out var selfHref))
            {
                var document = await Http.GetFromJsonAsync<DocumentLinksResponse>(selfHref.TrimStart('/'));
                Detail.GenericActions = document?.Links?
                    .Where(l => !string.IsNullOrEmpty(l.Label)
                        && !string.Equals(l.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? [];
                StateHasChanged();
            }
        }
        catch (HttpRequestException e)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    private void ClearDetail()
    {
        _selectedItem = null;
        Detail.Node = null;
        Detail.MaskName = null;
        Detail.VersionNumber = null;
        Detail.IndexData = null;
        Detail.Tags = null;
        Detail.GenericActions = []; // an action must not outlive its subject (ADR 0559)
        _downloadUrl = null;
        ClearPreviewPane();
        _selectedNode = null;
        _comments = [];
        _replyingTo = null;
        _newComment = string.Empty;
        _replyText = string.Empty;

        Detail.IsEditing = false;
        Detail.SysHasVersion = false;
        Detail.SysWorkflowStatus = null;
        Detail.SysCurrentVersionId = Guid.Empty;
        Detail.SysDocumentDateHref = null; // an address must not outlive its subject (ADR 0559)
        Detail.SysCurrentVersion = null;
        Detail.VersionCount = 0;
        Detail.SysName = string.Empty;
        Detail.SysFileExtension = string.Empty;
        Detail.SysDocumentDate = null;
        Detail.SysCreated = string.Empty;
        Detail.SysCreatedBy = string.Empty;
        Detail.SysHasTiff = false;
        Detail.SysOcrCodes = [];
        Detail.Retention = null;
        Detail.Subscribed = false;
        Detail.EditFields.Clear();

        // The addresses and the rights the PREVIOUS subject advertised. Clearing them is what stops an action
        // being offered — and worse, taken — against the wrong document while the new subject is still loading:
        // a rel that has not arrived means "not available to you, here, now" (ADR 0543), which is exactly true
        // during a load. Leaving them set kept the Manage access button rendered from the last subject's rights
        // and opened the dialog on the last subject's grants.
        Detail.Links = null;
        Detail.CanManagePermissions = false;
        Detail.CanEditIndexData = false;
        Detail.BreaksInheritance = false;
        Detail.ExternalLinksHref = null;
        Detail.MaskDefinitionHref = null;
        Detail.WorkflowLinks = null;
    }

    // Open the reminder (Wiedervorlage) dialog for the selected document (ADR "Document reminders").

    // These actions belong to DocumentActions now (ADR 0558) — sharing, reminders and following are things the
    // user does to a document they picked, like rename and move beside them. What stays here is the state the
    // SHELL renders from: whether the ribbon draws the my-links button, and whether the chat header's bell is lit.
    private string? _myExternalLinksHref;
    private bool _folderSubscribed;

    private Task OpenBookingsDialogAsync() =>
        Detail.Links is { } links && links.TryGetValue("bookings", out var href)
            ? Actions.OpenBookingsAsync(Detail.SysName, href)
            : Task.CompletedTask;

    private Task OpenExternalLinksDialogAsync() =>
        _selectedItem is { } item && Detail.ExternalLinksHref is { } href
            ? Actions.OpenExternalLinksAsync(item.Id, Detail.SysName, href)
            : Task.CompletedTask;

    private async Task OpenMyExternalLinksAsync()
    {
        if (_myExternalLinksHref is { } href && await Actions.OpenMyExternalLinksAsync(href) is { } target)
        {
            await NavigateToDocumentAsync(target.DocumentId, target.ParentId);
        }
    }

    // Addressed from the ROW (ADR 0559): this button is ungated, so it is clickable while the detail load is
    // still in flight, and reading Detail.Links there handed the dialog a null href it then silently did
    // nothing with — 8 failures in 10 isolation runs, mis-filed as flakiness for months (#420). The
    // external-links button beside it is safe because it is GATED on its href, so a load hides it entirely.
    // The account menu opens the same dialog; the ribbon is the discoverable place for it — next to the other
    // things a user does with their documents rather than buried under their name (#461).
    private async Task OpenWebDavAsync() =>
        await (await DialogService.ShowAsync<Dialogs.WebDavDialog>(Strings.Get("DlgWebDav"))).Result;

    private async Task OpenReminderDialogAsync()
    {
        if (_selectedItem is not { } item)
        {
            return;
        }

        var href = Detail.Links?.GetValueOrDefault("reminders")
            ?? await Browse.FetchRelAsync(item.Id, "reminders");
        await Actions.OpenReminderAsync(item.Id, item.Name, href);
    }

    private async Task ToggleSubscriptionAsync()
    {
        if (_selectedItem is not { } item)
        {
            return;
        }

        // Resolved for the ROW the user clicked, not from the pane's state: this button is clickable while that
        // row's detail load is still in flight, and Detail.Links is null until it lands. Falling back to a fetch
        // costs one request in that window and is always the right document, where reading the pane would be
        // either stale or absent (ADRs 0543/0555/0559 — hold the row).
        var href = Detail.Links?.GetValueOrDefault("subscription")
            ?? await Browse.FetchRelAsync(item.Id, "subscription");
        if (await Actions.ToggleSubscriptionAsync(href, Detail.Subscribed, isFolder: false) is { } now)
        {
            Detail.Subscribed = now;
        }
    }

    private async Task LoadFolderSubscriptionAsync(BrowseNode folder) =>
        _folderSubscribed = await Actions.IsSubscribedAsync(await Browse.FetchRelAsync(folder.Id, "subscription"));

    private async Task ToggleFolderSubscriptionAsync()
    {
        if (_selectedNode is not { IsFolder: true } folder)
        {
            return;
        }

        var href = await Browse.FetchRelAsync(folder.Id, "subscription");
        if (await Actions.ToggleSubscriptionAsync(href, _folderSubscribed, isFolder: true) is { } now)
        {
            _folderSubscribed = now;
        }
    }

    // Manage-access affordance (ADR "Manage-access UI for document/folder ACLs"): the caller's own
    // CanManagePermissions on the selected item gates the button; BreaksInheritance is shown in the dialog.
    // Sensitivity label (ADR "Data classification / sensitivity labels"): current value + the staged edit value.
    // The selected document's sensitivity (ADR "Configurable sensitivity labels + upload defaults") — the per-
    // tenant label id/name/colour + whether it watermarks; DetailState.Edit* is the pending picker value.
    // A repeating diagonal SVG-background watermark of "<LABEL> · <viewer>" (ADR "Document watermarking"),
    // shown over the preview when the label's watermark flag is set. Client-side only (a screenshot deterrent /
    // classification reminder — bypassable, server-stamped downloads deferred).
    private string WatermarkStyle()
    {
        var text = System.Net.WebUtility.HtmlEncode($"{Detail.SensitivityName} · {_viewerName}");
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='360' height='200'><text x='10' y='150' fill='rgba(130,130,130,0.20)' font-size='20' font-family='sans-serif' transform='rotate(-28 10 150)'>{text}</text></svg>";
        return $"background-image:url(\"data:image/svg+xml,{Uri.EscapeDataString(svg)}\");";
    }

    // Navigate the contents pane to a folder (from Go to / References / Search), optionally selecting an item.
    // Slice simplification: the tree isn't re-synced/expanded to the folder, matching the desktop.
    private async Task NavigateToFolderAsync(Guid folderId, Guid? selectItemId = null)
    {
        if (_activeTab != Tab.Repositories)
        {
            await SetTab(Tab.Repositories);
        }

        // ONE read, whose rels the node then carries (ADR 0557). This used to keep only the NAME, so the node
        // arrived address-less: LoadContentsAsync re-fetched the same resource for `children`, and every
        // rel-gated affordance read as unavailable on a folder reached by Go to or a search hit.
        var doc = await FetchFolderAsync(folderId);
        // The capabilities come from that SAME read (#858). Leaving them at their defaults here would repeat,
        // for the gates, exactly the bug the comment above describes for the links: a folder reached by Go to or
        // a search hit would show Rename / Move to / Delete as unavailable while the identical folder reached
        // through the tree offered them.
        await SelectFolderAsync(new BrowseNode(folderId, doc?.Name ?? "(folder)", true, false, false,
            CanDelete: doc?.CanDelete ?? false, CanCreateChildren: doc?.CanCreateChildren ?? false,
            CanEditIndexData: doc?.CanEditIndexData ?? false,
            CanMove: Links.Href(doc?.Links, "move") is not null,
            CanManagePermissions: doc?.CanManagePermissions ?? false,
            Links: Links.RelMap(doc?.Links)));

        // Prefer the item's real row; fall back to its reference (shortcut) row when the folder holds only a
        // shortcut (a referencing folder) — selecting a reference loads the target document for viewing.
        if (selectItemId is { } id
            && (_folderContents.FirstOrDefault(n => n.Id == id && !n.IsReference)
                ?? _folderContents.FirstOrDefault(n => n.Id == id)) is { } target)
        {
            await SelectRowAsync(target);
        }

        StateHasChanged();
    }

    // The resource behind a folder id, links and all: its name, its children address and which creates it admits
    // all ride in the one response (ADR 0557).
    private async Task<DocumentLinksResponse?> FetchFolderAsync(Guid id)
    {
        try
        {
            // Through the single sanctioned id-to-resource fetch (BrowseService), not a private copy of the
            // same GET — two copies of the one address the client may build is how the exception multiplies.
            return await Browse.FetchAsync(id);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // Takes the document's advertised address (its `self` rel), so the concurrency probe hits the same resource
    // the mutation below will — rather than a path rebuilt from an id (ADR 0543, issue #416).

    // Tag chip editor + catalog autocomplete (ADR "Document tags"). SearchFunc suggests not-yet-added catalog
    // tags; Enter commits the box (a coerced free-form value or the highlighted suggestion).
    // Tag entry forwards to the editor, which owns the working set: it suggests catalogue tags not already on
    // the document, and Enter commits the box exactly as the explicit Add button does.

    // Two tabs' "Go to": a search hit and a legal-hold review finding. Both are the same act -- turn a row the
    // user is looking at on ANOTHER tab into a folder to open and an item to select -- so they are one line of
    // reasoning each on top of NavigateToFolderAsync, and they belong beside it rather than beside the tabs
    // that raise them.
    private Task OpenHeldDocumentAsync(LegalHoldItemDto item) =>
        item.ParentId is { } parentId ? NavigateToFolderAsync(parentId, item.DocumentId) : NavigateToFolderAsync(item.DocumentId);

    private async Task OpenSearchResultAsync(SearchHit hit)
    {
        if (hit.IsFolder)
        {
            await NavigateToFolderAsync(hit.Id);
        }
        else if (hit.ParentId is { } parentId)
        {
            await NavigateToFolderAsync(parentId, hit.Id);
        }
        else
        {
            await NavigateToFolderAsync(hit.Id);
        }
    }
}
