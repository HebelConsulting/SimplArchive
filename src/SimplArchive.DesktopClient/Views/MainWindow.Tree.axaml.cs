using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The repository tree's handlers (#519 continues #466's split of this code-behind by feature): the tap that
// re-shows a folder's contents, the context menu itself, and everything that menu offers -- rename, delete,
// upload, move, sort order, legal hold, place reference, references, manage access, take over, refresh and
// follow.
//
// Same class, so the context-node field and the dialog helpers stay reachable without being passed anywhere.
public partial class MainWindow
{
    // Tap a tree folder → re-show its contents even if it's already the selected node (re-syncs the list
    // after the contents pane drilled elsewhere). ReselectTreeFolderAsync no-ops for a different node or when
    // the list already shows this one, so a genuinely new selection stays with OnSelectedTreeNodeChanged.
    private void OnTreeItemTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() is not { DataContext: TreeNodeViewModel node } item)
        {
            return;
        }

        // A single click opens (expands) the folder to reveal its sub-folders — unless the click was on the
        // expand/collapse chevron, which toggles it itself so collapsing still works.
        var onChevron = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Avalonia.Controls.Primitives.ToggleButton>().Any() ?? false;
        if (!onChevron && !node.IsSynthetic && !node.IsLauncher)
        {
            node.IsExpanded = true;
        }

        Safe.Fire(() => vm.ReselectTreeFolderAsync(node));
    }

    // Right-clicking a tree folder targets it for the context-menu actions (and selects it so the New-subfolder
    // action files into it). Suppress the menu over empty space / synthetic nodes.
    private TreeNodeViewModel? _treeContextNode;

    internal void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _treeContextNode = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as TreeNodeViewModel;
        if (_treeContextNode is null or { IsSynthetic: true } or { IsLauncher: true } && DataContext is MainWindowViewModel)
        {
            e.Handled = true; // no folder / a launcher node under the cursor — don't show the folder menu
            return;
        }

        if (DataContext is MainWindowViewModel vm && _treeContextNode is { } node)
        {
            vm.SelectedTreeNode = node;
            vm.TreeContextHasReferences = node.HasReferences;
            // Read from the RIGHT-CLICKED node, not from pane state (ADR 0559): the pane describes whatever
            // last finished loading, which during a load is a different folder than the one under the cursor.
            vm.TreeContextCanCreateChild = node.CanCreateChildren;
            vm.TreeContextCanTakeOver = node.HasRel("take-over");
            // The destructive half, from the same right-clicked node and for the same reason (#858).
            vm.TreeContextCanEditIndexData = node.CanEditIndexData;
            vm.TreeContextCanMove = node.CanMove;
            vm.TreeContextCanDelete = node.CanDelete;
            vm.TreeContextCanManageAccess = node.CanManagePermissions;

            // Built from what the node ADMITS rather than from rels the client knows by name (#673): the server
            // sends the label and the address, so this loop needs no case per family and a mask nobody
            // hardcoded still gets an entry.
            vm.TreeContextAdmits = [.. node.Admits.Select(a =>
                TreeMenuEntry.Create(a.Name,
                    // The glyph the thing will WEAR once it exists — and the FALLBACK is the plain kind
                    // glyph, not an add-glyph. These entries sit under "New", so the verb is already said:
                    // a plus on one of them made Folder read as a different sort of action from its
                    // siblings, which wear their mask's own icon. Seen in the built menu, not in a diff.
                    Services.MaskIcon.For(a.Icon) ?? (a.Folder ? "mdi-folder" : "mdi-file-document-outline"),
                    () => Safe.Fire(() => CreateAdmittedAsync(vm, node, a))))];
            vm.TreeContextCanCreateAny = vm.TreeContextAdmits.Count > 0;
        }
    }

    // One create for every kind the folder offered. The address, the label and — since the client cannot know
    // the mask — WHICH QUESTION TO ASK all come from the entry the server sent, so this needs no case per
    // family and a mask nobody hardcoded still works.
    private async Task CreateAdmittedAsync(MainWindowViewModel vm, TreeNodeViewModel node, Services.CreatableChild admitted)
    {
        if (admitted.Prompt == "note")
        {
            if (await new NewNoteDialog().ShowDialog<NewNoteDialog.Result?>(this) is { } note)
            {
                await vm.CreateNoteAsync(node.Id, admitted.Href, note.Title, note.Body);
            }

            return;
        }

        // A person and an event get the Contacts/Calendar tabs' OWN dialogs (#689), reused rather than
        // reimplemented: two forms for one object is how they come to disagree about which fields are
        // required. The folder is handed in as the single target, which both fixes the destination and hides
        // the dialog's collection picker (it draws only above one). Nothing is created until Save.
        if (admitted.Prompt is "contact" or "appointment")
        {
            await TreeCreateDialogs.CreateAsync(this, vm, node, admitted);
            return;
        }

        // Titled with the mask's own name, so the user is told which of the kinds on the menu they picked —
        // and so a tenant-authored folder mask reads correctly without a string of its own.
        var name = await new NewFolderDialog(admitted.Name, admitted.Name).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            // The href the ENTRY carried, never one composed from the node: following the address that granted
            // the affordance is what stops the gate and the action drifting apart (ADR 0543/0559).
            await vm.CreateSubfolderAsync(node.Id, admitted.Href, name, admitted.MaskId);
        }
    }


    internal void OnTreeRename(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node)
        {
            return;
        }

        var name = await new RenameDialog(node.Name).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name) && name != node.Name)
        {
            await vm.RenameFolderAsync(node.Href("self"), name);
        }
    });

    internal void OnTreeDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && _treeContextNode is { } node
            && await new ConfirmDialog($"Delete the folder '{node.Name}' and everything inside it? It will be moved to the recycle bin.", "Delete").ShowDialog<bool>(this))
        {
            await vm.DeleteFolderAsync(node.Id, node.Href("self"));
        }
    });

    // ---- The rest of the tree context menu's folder actions (ADR "Tree-pane context menu") ----------------
    // Each targets the RIGHT-CLICKED node (_treeContextNode), not the contents-list SelectedItem — a tree
    // right-click means "this folder". The selection-scoped ones (Export, folder sort) work off the fact that
    // OnTreeContextRequested has already made the node the current selection.

    // Upload files into the right-clicked folder. Reuses the drag-and-drop path (UploadDroppedFilesAsync), so
    // duplicate detection and per-file error reporting behave identically to a drop.
    internal void OnTreeUpload(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = SimplArchive.Localization.Strings.Get("Upload"),
            AllowMultiple = true,
        });
        if (files.Count > 0)
        {
            await vm.UploadDroppedFilesAsync(files, node.Links);
        }
    });

    internal void OnTreeMove(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node
            || vm.CreateMoveTargetPickerViewModel() is not { } picker)
        {
            return;
        }

        if (await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this) is { } result)
        {
            await vm.MoveFolderAsync(node.Href("self"), node.Name, result.TargetId);
        }
    });

    // Puts the detail pane's folder-settings row into edit mode for the right-clicked folder (its contents sort
    // order) — the same editor its "Edit" button opens.
    internal void OnTreeFolderSort(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.BeginFolderSortEditCommand.Execute(null);
        }
    }

    internal void OnTreePlaceLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node)
        {
            return;
        }

        if (await new LegalHoldDialog(node.Name).ShowDialog<LegalHoldDialog.Result?>(this) is { } result)
        {
            await vm.CreateLegalHoldAsync(result.Name, result.Reason, node.Id);
        }
    });

    // Place a reference (shortcut) to the right-clicked folder in another folder — the picker chooses where.
    internal void OnTreePlaceReference(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node
            || vm.CreateMoveTargetPickerViewModel() is not { } picker)
        {
            return;
        }

        if (await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this) is { } result)
        {
            await vm.PlaceReferenceAsync(node.Id, node.Name,
                result.TargetLinks?.GetValueOrDefault("references")
                ?? throw new InvalidOperationException("The picked folder advertised no 'references' rel (ADR 0543/0555)."));
        }
    });

    internal void OnTreeReferences(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node
            || vm.CreateReferencesViewModel(node.Id, node.Name, node.DocumentSelfHref) is not { } references)
        {
            return;
        }

        await references.LoadAsync();
        var result = await new ReferencesDialog { DataContext = references }.ShowDialog<ReferencesDialogResult?>(this);
        if (result is { } r)
        {
            if (r.Promote)
            {
                await vm.PromotePrimaryLocationAsync(references.DocumentSelfHref, references.ItemId, r.FolderId,
                    r.FolderHref ?? throw new InvalidOperationException("The referencing-folder row advertised no 'open' rel (ADR 0543/0555)."));
            }
            else
            {
                await vm.OpenFolderAsync(
                    r.FolderHref ?? throw new InvalidOperationException("The referencing-folder row advertised no 'open' rel (ADR 0543/0555)."),
                    references.ItemId);
            }
        }
    });

    // Manage access on the right-clicked TREE folder (ADR "Tree-pane context menu with manage-access"). The
    // contents-list menu's OnManageAccess acts on SelectedItem; a tree node isn't a list row, so this targets
    // _treeContextNode instead. The dialog self-gates on the caller's own CanManagePermissions.
    internal void OnTreeManageAccess(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api || _treeContextNode is not { } node)
        {
            return;
        }

        var mvm = new ManageAccessViewModel();
        await mvm.SetupAsync(api, node.DocumentSelfHref, node.Name);
        await new ManageAccessDialog(mvm).ShowDialog(this);
    });

    // Take over a user's personal space (ADR 0672). The href comes from the RIGHT-CLICKED node, never from
    // pane state and never composed — the listing advertised it, and only to a caller who may perform it.
    internal void OnTreeTakeOver(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node || !node.HasRel("take-over"))
        {
            return;
        }

        var message = string.Format(Strings.Get("TakeOverConfirm"), node.Name);
        if (await new ConfirmDialog(message, Strings.Get("CtxTakeOver")).ShowDialog<bool>(this))
        {
            await vm.TakeOverPersonalSpaceAsync(node.Name, node.Href("take-over"));
        }
    });

    internal void OnTreeRefresh(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.RefreshCommand.ExecuteAsync(null);
        }
    });

    internal void OnTreeToggleFollow(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && _treeContextNode is { IsSynthetic: false } node)
        {
            await vm.ToggleFolderSubscriptionAsync(node.DocumentSelfHref);
        }
    });
}
