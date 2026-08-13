using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

#pragma warning disable CS0618 // DragEventArgs.Data / DataFormats.Files — the pre-DataTransfer drag API, as in the original code-behind

// The workbench's drag-and-drop machinery (issue #466 moved it out of MainWindow's code-behind): OS-file drops
// onto the list/tree/inbox, and the internal move/reference drag with its press-snapshot state (ADR "Desktop
// drag-and-drop move and reference"; the selection snapshot exists because a multi-select ListBox collapses to
// the pressed row before the drag threshold — see the memory that cost a session). A real collaborator rather
// than a partial file: its six state fields and tunnel handlers are one lifecycle, coupled to the window only
// through three named controls and the _window.DataContext.
internal sealed class WorkbenchDragDrop
{
    private readonly MainWindow _window;
    private readonly ListBox _contentsList;
    private readonly TreeView _folderTree;
    private readonly ListBox _serverInboxList;

    public WorkbenchDragDrop(MainWindow window, ListBox contentsList, TreeView folderTree, ListBox serverInboxList)
    {
        _window = window;
        _contentsList = contentsList;
        _folderTree = folderTree;
        _serverInboxList = serverInboxList;
    }

    // The handler wiring MainWindow's constructor used to do inline.
    public void Wire()
    {
        _contentsList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _contentsList.AddHandler(DragDrop.DropEvent, OnDrop);
        _contentsList.AddHandler(InputElement.PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _contentsList.AddHandler(InputElement.PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        _contentsList.AddHandler(InputElement.PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
        _folderTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        _folderTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        _folderTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        _folderTree.AddHandler(InputElement.PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        _folderTree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        _serverInboxList.AddHandler(DragDrop.DragOverEvent, OnInboxDragOver);
        _serverInboxList.AddHandler(DragDrop.DropEvent, OnInboxDrop);
    }

    // Custom drag payload for an internal move/reference drag (distinct from an OS-file drop). See ADR
    // "Desktop drag-and-drop move and reference".
    private const string NodeDragFormat = "simplarchive/node";

    // One dragged item — the internal drag carries a LIST of these (the whole selection, or a single tree folder),
    // so a drop moves/references all of them. Id is the underlying item (a reference's target), used for the ops.
    private sealed record DragNode(Guid Id, string Name, bool IsFolder, bool IsReference);

    // The nodes of an in-flight internal move/reference drag, held in a field (NOT as a DataObject format) so the
    // drag's DataObject carries ONLY the staged OS files — then the macOS drag badge shows the real item count
    // instead of the number of data formats (which was always 2: node-format + files). Set at drag-start, read by
    // the drop handlers to tell an internal drag from an OS-file drop, cleared when the gesture ends.
    private List<DragNode>? _internalDrag;

    private NodeViewModel? _dragCandidate;

    private Point _dragStart;

    // The multi-selection captured at pointer-PRESS. A plain press on an already-selected row makes the ListBox
    // collapse its selection to that one row before the drag threshold is crossed, so reading SelectedItems at
    // drag-start would see only one item. The tunnel press handler fires before that collapse, so we snapshot here.
    private List<NodeViewModel> _dragSelection = [];

    private TreeNodeViewModel? _treeDragCandidate;

    private Point _treeDragStart;

    private void OnInboxDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnInboxDrop(object? sender, DragEventArgs e) => Safe.Fire(async () =>
    {
        if (_window.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var storageFiles = e.Data.GetFiles()?.OfType<IStorageFile>().ToList();
        if (storageFiles is not { Count: > 0 })
        {
            return;
        }

        await vm.UploadFilesToInboxAsync(await ReadStorageFilesAsync(storageFiles));
    });

    // Begin an internal move/reference drag once the pointer leaves the pressed row by a small threshold.
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_contentsList).Properties.IsLeftButtonPressed
            && MainWindow.FindDataContext<NodeViewModel>(e.Source) is { } node)
        {
            _dragCandidate = node;
            _dragStart = e.GetPosition(_contentsList);
            // Snapshot the full multi-selection now — the ListBox is about to collapse it to the pressed row.
            _dragSelection = _contentsList.SelectedItems?.OfType<NodeViewModel>().ToList() ?? [];
        }
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e) => Safe.Fire(async () =>
    {
        if (_dragCandidate is not { } node)
        {
            return;
        }

        if (!e.GetCurrentPoint(_contentsList).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }

        var delta = e.GetPosition(_contentsList) - _dragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _dragCandidate = null;

        // The drag carries the whole multi-selection when the grabbed row is part of it (else just that row), so a
        // drop moves/references ALL of them — not only the one under the cursor. We use the press-time snapshot
        // (_dragSelection), since the ListBox has since collapsed its live selection to the pressed row. Archive
        // rows can't be dragged.
        var source = (_dragSelection.Count > 1 && _dragSelection.Contains(node) ? _dragSelection : [node])
            .Where(n => !n.IsArchiveEntry && !n.IsArchiveBack).ToList();
        if (source.Count == 0)
        {
            return;
        }

        // Re-apply the multi-selection the ListBox collapsed to the pressed row on press, so the dragged items stay
        // visibly highlighted (and the bulk bar stays up) throughout the drag — the user can see exactly what will
        // move/reference.
        if (source.Count > 1 && _contentsList.SelectedItems is { } sel)
        {
            sel.Clear();
            foreach (var n in source)
            {
                sel.Add(n);
            }
        }

        _internalDrag = source.Select(n => new DragNode(n.Id, n.Name, n.IsFolder, n.IsReference)).ToList();
        var data = new DataObject();

        // Also stage the selection as OS files so a drop OUTSIDE the app copies them to the filesystem (issue
        // #266): a document as its current-version file (<stem><ext>), a folder as a recursive .zip. Real
        // docs/folders only — a reference stays an internal-only drag. The files must exist before DoDragDrop, so
        // stage (await) first, with a brief "preparing…" status (an async download can't run during the gesture).
        // Only the staged files ride the DataObject — the internal payload lives in _internalDrag — so the drag
        // badge counts items, not formats.
        var fileCount = 0;
        if (_window.DataContext is MainWindowViewModel dragVm && dragVm.Api is { } dragApi)
        {
            var items = source.Where(n => !n.IsReference).Select(n => new DragOutItem(n.Id, n.Name, n.IsFolder)).ToList();
            if (items.Count > 0)
            {
                dragVm.Status = Strings.Get("StPreparingDrag");
                try
                {
                    var files = await DragOutStager.StageAsync(dragApi, items);
                    if (files.Count > 0)
                    {
                        data.Set(DataFormats.FileNames, files);
                        fileCount = files.Count;
                    }
                }
                catch (Exception)
                {
                    // Best-effort — the internal move/reference drag still works even if staging fails.
                }
                finally
                {
                    dragVm.Status = "";
                }
            }
        }

        // An all-references drag stages no files; give the DataObject the node format so the OS still starts a drag.
        if (fileCount == 0)
        {
            data.Set(NodeDragFormat, _internalDrag);
        }

        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _internalDrag = null;
        }
    });

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e) => _dragCandidate = null;

    // A real tree folder can be dragged onto another folder to move/reference it (synthetic/launcher/personal
    // nodes aren't real, movable folders, so they're not drag sources). Mirrors the list's candidate+threshold so
    // a plain click still selects/expands.
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_folderTree).Properties.IsLeftButtonPressed
            && MainWindow.FindDataContext<TreeNodeViewModel>(e.Source) is { IsSynthetic: false, IsLauncher: false, IsPersonal: false } node)
        {
            _treeDragCandidate = node;
            _treeDragStart = e.GetPosition(_folderTree);
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e) => Safe.Fire(async () =>
    {
        if (_treeDragCandidate is not { } node)
        {
            return;
        }

        if (!e.GetCurrentPoint(_folderTree).Properties.IsLeftButtonPressed)
        {
            _treeDragCandidate = null;
            return;
        }

        var delta = e.GetPosition(_folderTree) - _treeDragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _treeDragCandidate = null;

        _internalDrag = [new DragNode(node.Id, node.Name, true, node.IsReference)];
        var data = new DataObject();

        // Stage the folder as a recursive .zip so a drop OUTSIDE the app copies it out (issue #266), unless it's a
        // shortcut (internal-only). Only the staged file rides the DataObject (the payload is in _internalDrag).
        var fileCount = 0;
        if (!node.IsReference && _window.DataContext is MainWindowViewModel dragVm && dragVm.Api is { } dragApi)
        {
            dragVm.Status = Strings.Get("StPreparingDrag");
            try
            {
                var files = await DragOutStager.StageAsync(dragApi, [new DragOutItem(node.Id, node.Name, true)]);
                if (files.Count > 0)
                {
                    data.Set(DataFormats.FileNames, files);
                    fileCount = files.Count;
                }
            }
            catch (Exception)
            {
                // Best-effort — the internal move/reference drag still works even if staging fails.
            }
            finally
            {
                dragVm.Status = "";
            }
        }

        if (fileCount == 0)
        {
            data.Set(NodeDragFormat, _internalDrag);
        }

        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _internalDrag = null;
        }
    });

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e) => _treeDragCandidate = null;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _internalDrag is not null || e.Data.Contains(DataFormats.Files) || e.Data.Contains(NodeDragFormat)
            ? DragDropEffects.Copy | DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e) => Safe.Fire(async () =>
    {
        if (_window.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Internal move/reference drag (identified by the in-flight _internalDrag field, not a DataObject format):
        // the dragged items dropped on a folder row file into that folder; dropped anywhere else in the pane file
        // into the currently-open folder.
        if (_internalDrag is { } dragged)
        {
            var node = MainWindow.FindDataContext<NodeViewModel>(e.Source);
            var targetFolderId = node is { IsFolder: true } ? node.Id : vm.CurrentFolderId;
            if (targetFolderId is { } folderId)
            {
                await PerformDropAsync(vm, dragged, folderId);
            }

            return;
        }

        var files = e.Data.GetFiles()?.OfType<IStorageFile>().ToList();
        if (files is not { Count: > 0 })
        {
            return;
        }

        var target = MainWindow.FindDataContext<NodeViewModel>(e.Source);

        // Dropped onto a document row → the inbox-style filing dialog: file as a new version of it, or into its
        // folder, with an optional comment (ADR "List-pane drop filing").
        if (target is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false }
            && vm.CreateDropFilingPickerViewModel(target, files.Count) is { } picker)
        {
            await picker.LoadAsync();
            var result = await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(_window);
            if (result is not null)
            {
                await vm.FileDroppedFilesAsync(files, result);
            }

            return;
        }

        // Dropped onto a folder row → that folder; anywhere else → the currently-open folder.
        await vm.UploadDroppedFilesAsync(files, target is { IsFolder: true } ? target.Id : null);
    });

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        // The tree accepts an internal move/reference drag, and — since #467 — an OS-file drop, matching the web
        // client. What it accepts depends on the node under the pointer, so that a target which cannot honour a
        // drop does not advertise one (ADR 0543 applied to the affordance).
        if (_internalDrag is not null || e.Data.Contains(NodeDragFormat))
        {
            var node = MainWindow.FindDataContext<TreeNodeViewModel>(e.Source);

            // Personal ▸ Inbox takes a document as a TEMPLATE (a copy, hence Copy); a real folder takes a move
            // or a reference; Check-out takes neither — a document already in the archive is not a working copy.
            e.DragEffects = node switch
            {
                { PersonalKind: "inbox" } => DragDropEffects.Copy,
                { PersonalKind: "checkout" } => DragDropEffects.None,
                _ => DragDropEffects.Copy | DragDropEffects.Move,
            };
            return;
        }

        if (e.Data.Contains(DataFormats.Files))
        {
            // Every folder takes files; so do both launchers, each meaning something different (see OnTreeDrop).
            var node = MainWindow.FindDataContext<TreeNodeViewModel>(e.Source);
            e.DragEffects = node is null ? DragDropEffects.None : DragDropEffects.Copy;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e) => Safe.Fire(async () =>
    {
        if (_window.DataContext is not MainWindowViewModel vm || MainWindow.FindDataContext<TreeNodeViewModel>(e.Source) is not { } treeNode)
        {
            return;
        }

        // An internal drag: onto Personal ▸ Inbox it copies the document in as a TEMPLATE, carrying its mask and
        // index values, so new work can start from an existing document without creating one (#467). Onto a real
        // folder it moves or references, as before.
        if (_internalDrag is { } dragged)
        {
            if (treeNode.PersonalKind == "inbox")
            {
                await vm.CopyDocumentsToInboxAsync(dragged.Select(d => d.Id).ToList());
                return;
            }

            if (!treeNode.IsLauncher)
            {
                await PerformDropAsync(vm, dragged, treeNode.Id);
            }

            return;
        }

        var storageFiles = e.Data.GetFiles()?.OfType<IStorageFile>().ToList();
        if (storageFiles is not { Count: > 0 })
        {
            return;
        }

        // The launchers each mean something specific, and neither can show its own result — the tree lists
        // FOLDERS — so each opens the tab that can (#467).
        if (treeNode.IsLauncher)
        {
            var files = await ReadStorageFilesAsync(storageFiles);
            if (treeNode.PersonalKind == "inbox")
            {
                await vm.UploadFilesToInboxAsync(files);
            }
            else
            {
                await vm.StashDroppedFilesAsync(files);
            }

            vm.SelectedTab = treeNode.LauncherTab;
            return;
        }

        // A plain folder node: file into THAT folder, then follow to it, so the user sees what they filed
        // rather than being left looking at whatever folder was open.
        await vm.UploadDroppedFilesAsync(storageFiles, treeNode.Id);
        await vm.OpenFolderAsync(treeNode.Id);
    });

    // Dragged real items offer Move or Reference; a drag of only shortcuts only ever places more shortcuts (moving
    // the real targets when you grabbed shortcuts would be surprising). Drops onto self are filtered out; the
    // backend additionally skips a folder dropped into its own subtree (cycle) and no-op re-references.
    private async Task PerformDropAsync(MainWindowViewModel vm, IReadOnlyList<DragNode> dragged, Guid targetFolderId)
    {
        var items = dragged.Where(d => d.Id != targetFolderId).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(d => d.Id).ToList();

        // An all-shortcuts drag only ever places references.
        if (items.All(d => d.IsReference))
        {
            await vm.BulkReferenceNodesAsync(ids, targetFolderId);
            return;
        }

        var label = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        var where = items.Count == 1 ? "it is" : "they are";
        var action = await new DropActionDialog(
            $"Move {label} here, or place a reference (shortcut) that leaves {(items.Count == 1 ? "it" : "them")} where {where}?")
            .ShowDialog<DropAction?>(_window);

        switch (action)
        {
            case DropAction.Move:
                await vm.BulkMoveNodesAsync(ids, targetFolderId);
                break;
            case DropAction.Reference:
                await vm.BulkReferenceNodesAsync(ids, targetFolderId);
                break;
        }
    }
    // Reading the bytes is shared with the inbox pane's own drop handler; a copy would be a second place to get
    // stream disposal wrong.
    private static async Task<List<(string Name, byte[] Bytes)>> ReadStorageFilesAsync(IReadOnlyList<IStorageFile> storageFiles)
    {
        var files = new List<(string Name, byte[] Bytes)>();
        foreach (var file in storageFiles)
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            files.Add((file.Name, buffer.ToArray()));
        }

        return files;
    }
}
#pragma warning restore CS0618
