using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Mutating an item from a CONTEXT MENU: create a subfolder, a section, a note or a structured child under it;
// rename it; move it; place a reference to it; delete it; follow or unfollow it.
//
// The distinguishing feature is the target. These act on a node the user right-clicked -- addressed by the
// href that node already carries (ADR 0555) -- rather than on the currently-open folder or the selection,
// which is why CreateFolderAsync stayed behind: it creates in the open folder and belongs with the shell.
//
// The heading this came from was accurate, which is rare in this file: it is the SECOND of the six sections
// taken out of this view model that did not have to be dealt out into several homes first (#941). Its only
// stretch is that three members take a NodeViewModel (a contents-list row) rather than a tree node, but a row
// menu and a tree menu are the same act on the same document, so keeping them together is the point rather
// than an oversight.
//
// A partial rather than a type of its own: each action ends by refreshing this view model's tree or contents
// list and reporting to its status line.
public sealed partial class MainWindowViewModel
{
    // Create a subfolder directly under a tree folder (not necessarily the currently-open one) — through the
    // node's own children address (ADR 0555); the id only drives the local tree refresh.
    public Task CreateSubfolderAsync(Guid parentId, string childrenHref, string name, Guid? maskId = null) =>
        CreateChildAsync(parentId, api => api.Documents.CreateFolderAsync(childrenHref, name, maskId), "StCreatedFolder", "StErrCreateFolder", name);

    // A section, and a note, inside a notebook (#564). Both reach the server through an href the row itself
    // advertised — the caller never names a mask, so the rule about what may live where stays on the server.
    public Task CreateSectionAsync(Guid parentId, string sectionsHref, string name) =>
        CreateChildAsync(parentId, api => api.Documents.CreateSectionAsync(sectionsHref, name), "StCreatedSection", "StErrCreateSection", name);

    public Task CreateNoteAsync(Guid parentId, string notesHref, string title, string body) =>
        CreateChildAsync(parentId, api => api.Documents.CreateNoteAsync(notesHref, title, body), "StCreatedNote", "StErrCreateNote", title);

    // A contact, and an appointment, from the tree (#689). The whole filled-in resource goes in one request —
    // nothing exists until the user saves the dialog, so a cancelled one leaves no stub for a DAV client to
    // sync. Their messages name the FOLDER as well as the item, which is why inFolder exists at all: from a
    // tree menu the folder the user aimed at is the only thing distinguishing this from the tab's own create.
    public Task CreateStructuredChildAsync(
        Guid parentId, string createHref, object payload, string okKey, string errKey, string name, string inFolder) =>
        CreateChildAsync(
            parentId, api => api.StructuredEditors.CreateAsync(createHref, payload), okKey, errKey, name, inFolder);

    // The creates differ only in the call and the two strings, so they share one body rather than becoming
    // copies that drift (the fourth would get the fix and the first three would not). What genuinely differs
    // rides in as a lambda at each call site, where a reader wants both the difference and the delegation on
    // one line.
    private async Task CreateChildAsync(
        Guid parentId, Func<SimplArchiveApiClient, Task> create, string okKey, string errKey, string name,
        string? inFolder = null)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await create(_api);
            // Two-argument where the caller named a folder, one where it did not — the message templates differ
            // in arity, and string.Format throws on a template whose placeholders outnumber its arguments.
            Status = inFolder is null
                ? string.Format(Strings.Get(okKey), name)
                : string.Format(Strings.Get(okKey), name, inFolder);
            await ShowNewChildInTreeAsync(parentId);
            if (_currentFolderId == parentId)
            {
                await LoadFolderContentsAsync(parentId);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get(errKey), e.Message); }
    }

    // Rename a tree folder by id (rebuilds the tree so its node label updates, unlike the list-row rename).
    public async Task RenameFolderAsync(string documentSelfHref, string newName)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.RenameAsync(documentSelfHref, newName);
            Status = string.Format(Strings.Get("StRenamedTo"), newName);
            await ReloadTreeAsync();
            if (_currentFolderId is { } current)
            {
                await LoadFolderContentsAsync(current);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrRename"), e.Message); }
    }

    // Move a TREE folder (and its subtree) under another folder, by id — the tree context menu's "Move to…"
    // (ADR "Tree-pane context menu"). Unlike MoveNodeAsync (a dragged contents-list row) the tree itself
    // changes shape, so this reloads the tree as well as the open folder's contents.
    public async Task MoveFolderAsync(string documentSelfHref, string folderName, Guid targetFolderId)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.MoveAsync(documentSelfHref, targetFolderId);
            Status = string.Format(Strings.Get("StMoved"), folderName);
            await ReloadTreeAsync();
            if (_currentFolderId is { } current)
            {
                await LoadFolderContentsAsync(current);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrMove"), e.Message); }
    }

    // Place a reference (shortcut) to a TREE folder into another folder, by id — the tree context menu's
    // "Place reference…". The referenced folder shows up in the target's subtree (ADR "Referenced folder in the
    // tree"), so the tree is reloaded too.
    public async Task PlaceReferenceAsync(Guid folderId, string folderName, string targetReferencesHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.References.CreateReferenceAsync(targetReferencesHref, folderId);
            Status = string.Format(Strings.Get("StPlacedRef"), folderName);
            await ReloadTreeAsync();
            if (_currentFolderId is { } current)
            {
                await LoadFolderContentsAsync(current);
            }
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrPlaceRef"), e.Message); }
    }

    // Soft-delete a tree folder (and its subtree) to the recycle bin by id.
    public async Task DeleteFolderAsync(Guid folderId, string documentSelfHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.DeleteAsync(documentSelfHref);
            Status = Strings.Get("StFolderDeleted");
            if (_currentFolderId == folderId)
            {
                _currentFolderId = null;
                Items.Clear();
                ClearDetail();
            }

            await ReloadTreeAsync();
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrDeleteMsg"), e.Message); }
    }

    // Follow / unfollow a folder and its whole subtree (ADR "Folder / subtree subscriptions") — fetches the
    // current state and toggles it, so one menu item is always correct.
    public async Task ToggleFolderSubscriptionAsync(string documentSelfHref)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            // ONE fetch of the folder's resource, then both halves follow its advertised address (ADR 0557).
            var subscriptionHref = await _api.Documents.RelViaSelfAsync(documentSelfHref, "subscription");
            var following = await _api.Documents.GetSubscriptionAsync(subscriptionHref);
            await _api.Documents.SetSubscriptionAsync(subscriptionHref, !following);
            Status = !following ? "Following this folder and everything in it." : "Unfollowed folder.";
        }
        catch (Services.ApiActionException e) { Status = e.Message; }
        catch (Exception e) { Status = string.Format(Strings.Get("StErrSubscriptionMsg"), e.Message); }
    }

    // Renames a document/folder (the view collects the new name via a dialog and calls this). Reloads the
    // current folder so the new name shows. A renamed sub-folder's tree node stays stale until a refresh —
    // same whole-tree-reload simplification as upload.
    public async Task RenameNodeAsync(NodeViewModel node, string newName)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            await _api.Documents.RenameAsync(node.DocumentSelfHref, newName);
            Status = string.Format(Strings.Get("StRenamedTo"), newName);
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrRename"), e.Message);
        }
    }

    // Soft-deletes a document/folder to the recycle bin (the view confirms first and calls this). For a
    // reference row, removes only the shortcut (never the target) — see ADR "Desktop drag-and-drop move
    // and reference".
    public async Task DeleteNodeAsync(NodeViewModel node)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            // Branch on WHAT THE ROW IS first, and only then on whether it gave us an address. Folding the
            // href test into the `if` narrows it — and silently widens the `else`, which deletes the target
            // DOCUMENT. A rel may legitimately be absent (ADR 0543), so "reference without a delete address"
            // is a state that has to be handled, never one that falls through to a more destructive action.
            if (node.IsReference)
            {
                if (node.ReferenceDeleteHref is not { } referenceDeleteHref)
                {
                    Status = string.Format(Strings.Get("StErrDeleteMsg"), $"'{node.Name}' offered no way to remove the shortcut.");
                    return;
                }

                await _api.References.DeleteReferenceAsync(referenceDeleteHref);
                Status = string.Format(Strings.Get("StRemovedRef"), node.Name);
            }
            else
            {
                await _api.Documents.DeleteAsync(node.DocumentSelfHref);
                if (_selectedDocumentId == node.Id)
                {
                    ClearDetail();
                }

                Status = string.Format(Strings.Get("StDeleted"), node.Name);
            }

            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrDeleteMsg"), e.Message);
        }
    }

    // Moves (reparents) a dragged item into a folder (the view collects Move-vs-reference and calls this).
    public async Task MoveNodeAsync(NodeViewModel node, Guid targetFolderId)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            await _api.Documents.MoveAsync(node.DocumentSelfHref, targetFolderId);
            Status = string.Format(Strings.Get("StMoved"), node.Name);
            await LoadFolderContentsAsync(folderId);
        }
        catch (Services.ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrMove"), e.Message);
        }
    }
}
