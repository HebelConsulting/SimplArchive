using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

public partial class MainWindow : Window
{




    public MainWindow()
    {
        InitializeComponent();

        // Drag-and-drop — OS-file drops + the internal move/reference drag — lives in WorkbenchDragDrop
        // (issue #466); it wires its own handlers onto the list, tree and intray controls.
        new WorkbenchDragDrop(this, ListPane.List, TreePane.Tree, ServerIntrayList).Wire();

        // Ctrl/Cmd+P opens the server manager (ADR "Desktop server configuration"), Ctrl/Cmd+O opens the selected
        // document (#482) — a window-level tunnel handler so they fire regardless of focus.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);


        // Tapping a tree folder always shows its contents — even the already-selected node, so re-clicking the
        // tree re-syncs the list after drilling into a subfolder via the contents pane (the binding alone
        // short-circuits a same-node re-selection). See MainWindowViewModel.ReselectTreeFolderAsync.
        TreePane.Tree.AddHandler(Gestures.TappedEvent, OnTreeItemTapped);

        // Provide the sticky-note dialog to the Repositories/Intray preview (ADR "Document annotations"). Set on
        // the main Preview only, so the Recycle-bin preview never offers note editing. Kept in code-behind since
        // it opens an Avalonia Window owned by this window, keeping the view-model view-agnostic.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Preview.AnnotationDialog = ShowAnnotationDialogAsync;
                // Bring a newly marked tree node into view (#692's desktop half). Only the VIEW knows which
                // container renders which node, so the view-model raises and this scrolls. BringIntoView is
                // minimal-movement and a no-op when the node is already visible, which is the same behaviour
                // decided for the web: never move the pane without cause.
                vm.MarkedNodeChanged += node => Dispatcher.UIThread.Post(() =>
                    TreePane.Tree.GetVisualDescendants().OfType<Border>()
                        .FirstOrDefault(b => ReferenceEquals(b.DataContext, node))?.BringIntoView());
                vm.ExtendRetentionDialog = name => new ExtendRetentionDialog(name).ShowDialog<string?>(this);
                vm.Search.SaveSearchNamePrompt = () => new NewFolderDialog("Save search", "Name this saved search").ShowDialog<string?>(this);
                vm.DuplicateUploadDialog = req => new DuplicateUploadDialog(req).ShowDialog<MainWindowViewModel.DuplicatePromptResult?>(this);

                // The duplicate-address-claim question (#703): the client composed it, localized, from the
                // response's claimedBy — this only puts a window around it.
                vm.ConfirmDuplicateClaimDialog = question =>
                    new ConfirmDialog(question, Strings.Get("DupClaimConfirm")).ShowDialog<bool>(this);
                vm.NameConflictDialog = req => new NameConflictDialog(req).ShowDialog<Services.UploadConflictResolver.NameConflictChoice?>(this);
                vm.ShowReminderDialog = rvm => new ReminderDialog(rvm).ShowDialog(this);
                vm.ShowBookingDialog = bvm => new BookingDialog(bvm).ShowDialog(this);
                vm.ShowExternalLinksDialog = evm =>
                {
                    var window = new ExternalLinksDialog(evm);
                    // The view-model closes its own window for "Go to": the navigation it triggers happens in the
                    // workbench behind, which a dialog left open would be covering.
                    evm.RequestClose = window.Close;
                    return window.ShowDialog(this);
                };
                vm.ShowExternalLinkDetailDialog = dvm => new ExternalLinkDetailDialog(dvm).ShowDialog(this);
                vm.Search.ShowShareSavedSearchDialog = svm => new ShareSavedSearchDialog(svm).ShowDialog<bool>(this);
            }
        };
    }

    private async Task<PreviewViewModel.AnnotationDialogResult?> ShowAnnotationDialogAsync(PreviewViewModel.AnnotationDialogRequest request)
    {
        var result = await new AnnotationDialog(request.Text, request.Color, request.AuthorName, request.CanEdit, request.CanDelete, request.IsShape)
            .ShowDialog<AnnotationDialog.Result?>(this);
        return result is null ? null : new PreviewViewModel.AnnotationDialogResult(result.Action, result.Text, result.Color);
    }

    // Persist the pane layout (incl. GridSplitter drag-resizes) when the window closes, and guard against losing
    // un-backed-up check-out edits (ADR "Check-out working-copy stash + exit guard"): if any checked-out document
    // has edits that aren't saved to the cloud or checked in, cancel the close, switch to the Check-out tab, and
    // ask the user to resolve them first. Closing is the desktop's only exit (there is no separate logout).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Editing goes through the WebDAV mount, which saves straight to the cloud stash (ADR 0513), so there are no
        // un-backed-up local edits to guard against on close — just persist the layout and let the window close.
        (DataContext as MainWindowViewModel)?.SaveLayout();
        base.OnClosing(e);
    }

    // Ribbon "New folder": prompt for a name, then create it in the current folder.
    internal void OnNewFolder(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var name = await new NewFolderDialog().ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await vm.CreateFolderAsync(name);
        }
    });

    // Rename/Delete are triggered from the ribbon, the row context menu, and F2/Delete — all act on the
    // selected contents-list row and route through these code-behind handlers so the dialogs live in the view.
    internal void OnRename(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await RenameSelectedAsync(vm);
        }
    });

    internal void OnDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await DeleteSelectedAsync(vm);
        }
    });














    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") ----------------------------









    // Help ▸ Manual (ADR 0504): open the auto-generated user manual (served at /download/manual/ on the
    // connected server, ADR 0502) in the system browser rather than embedding a PDF viewer.
    internal void OnOpenManual(object? sender, RoutedEventArgs e) =>
        SystemBrowser.Open($"{DesktopClientOptions.ApiBaseUrl}/download/manual/SimplArchive-Manual.pdf");

    // Help ▸ Show log folder (ADR 0613). A log the user cannot find when support asks for it is a log that does
    // not exist — and asking somebody to launch a .app from a terminal is not a support procedure.
    internal void OnShowLogs(object? sender, RoutedEventArgs e) => NativeFileOpener.RevealDirectory(DesktopLog.Directory);

    // Help ▸ About (ADR 0504): the vendor block + the running client version.
    internal void OnShowAbout(object? sender, RoutedEventArgs e) =>
        Safe.Fire(async () => await new AboutDialog().ShowDialog(this));




    // ---- Legal holds (ADR "Legal hold & retention enforcement") ------------------------------------





    // Open (context menu): same as the ribbon Open button / double-click — a folder (or a document with
    // children, e.g. an email with attachments) drills in, a plain document opens in its native application.
    internal void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }









    // ---- Intray (ADR "S3-backed inbox", phase 2) -------------------------------------------------------

    // The one seam where the two surfaces differ (#521). A row's context menu carries the row as its Tag and
    // acts on THAT; a ribbon button carries none and acts on the current SELECTION. Both take their target
    // here and now — never from what the detail pane last finished loading, which is how permissions once got
    // granted against the wrong document (ADR 0559).
    //
    // Every handler already went through this helper, so the ribbon's five buttons needed no new code path:
    // the distinction lives in one expression rather than being re-decided per action.
    private IntrayItemViewModel? IntrayItemFrom(object? sender) =>
        (sender as Control)?.Tag as IntrayItemViewModel
        ?? ServerIntrayList.SelectedItem as IntrayItemViewModel;





    // Track the server-intray selection count so the "File multiple items" button shows only for 2+.
    private void OnServerIntraySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var selected = ServerIntrayList.SelectedItems?.Count ?? 0;
        vm.CanFileMultiple = selected >= 2;
        vm.Intray.Actions.SelectedCount = selected;

        // Ask what THIS row's pages can do. Fire-and-forget through Safe.Fire because a selection change must
        // not wait on a request; the buttons are cleared first, so during the flight they say "not available",
        // which is exactly true (ADR 0559).
        Safe.Fire(async () => await vm.Intray.Actions.LoadPagesAsync(
            selected == 1 ? ServerIntrayList.SelectedItem as IntrayItemViewModel : null));
    }


    // Edit the OCR languages (system field): the ordered multi-select picker (ADR "System fields +
    // OCR-language mask field").
    internal void OnEditOcrLanguages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var (catalog, selected) = vm.OcrLanguagePickerState();
        if (catalog.Count == 0)
        {
            return;
        }

        var picker = new OcrLanguagePickerViewModel(catalog, selected);
        var codes = await new OcrLanguagePickerDialog { DataContext = picker }.ShowDialog<List<string>?>(this);
        if (codes is not null)
        {
            vm.StageOcrLanguages(codes); // staged into the pane; the pane's Save persists it
        }
    });

    // Intray mask pane's OCR-language picker (ADR "Inbox OCR-language staging") — mirrors OnEditOcrLanguages.
    private void OnEditIntrayOcrLanguages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var (catalog, selected) = vm.Intray.OcrPickerState();
        if (catalog.Count == 0)
        {
            return;
        }

        var picker = new OcrLanguagePickerViewModel(catalog, selected);
        var codes = await new OcrLanguagePickerDialog { DataContext = picker }.ShowDialog<List<string>?>(this);
        if (codes is not null)
        {
            vm.Intray.StageOcrLanguages(codes); // staged into the pane; the pane's Save persists it
        }
    });

    // Tenant-admin tab: New repository — prompt for a name, then create a root-level document (ADR "Tenant-admin
    // settings tab"). Reuses the name-prompt dialog with a repository-specific title/label.
    internal void OnNewRepository(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var name = await new NewFolderDialog("New repository", "Repository name").ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await vm.CreateRepositoryAsync(name);
        }
    });

    // Tenant-admin tab: edit the default OCR languages via the shared ordered picker (ADR "Tenant-admin
    // settings tab"), staging the result into the pane; the pane's Save persists it.
    internal void OnEditTenantOcrLanguages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var (catalog, selected) = vm.TenantOcrPickerState();
        if (catalog.Count == 0)
        {
            return;
        }

        var picker = new OcrLanguagePickerViewModel(catalog, selected);
        var codes = await new OcrLanguagePickerDialog { DataContext = picker }.ShowDialog<List<string>?>(this);
        if (codes is not null)
        {
            vm.StageTenantOcrLanguages(codes);
        }
    });

    // Convert existing TIFFs (tenant-admin ribbon button): confirm with the pending count, then trigger the
    // backfill (ADR "Backfill searchable PDFs for existing TIFFs").
    internal void OnConvertExistingTiffs(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        int pending;
        try
        {
            pending = await vm.GetTiffBackfillPendingAsync();
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not check for documents to convert: {ex.Message}";
            return;
        }

        if (pending == 0)
        {
            vm.Status = "No documents need conversion.";
            return;
        }

        if (await new ConfirmDialog($"Convert {pending} existing scanned document(s) (TIFFs + scanned PDFs) to searchable PDFs?", "Convert").ShowDialog<bool>(this))
        {
            await vm.RunTiffBackfillAsync();
        }
    });

    // Export the selected repository/folder + subtree to a .zip (ADR "Repository export"). Tenant-admin-only.
    internal void OnExport(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var options = await new ExportDialog(vm.ExportRootName).ShowDialog<RepositoryArchiveClient.RepositoryExportOptions?>(this);
        if (options is null || vm.ExportRepositoryBytesAsync(options) is not { } bytesTask)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export repository",
            SuggestedFileName = $"{vm.ExportRootName}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
        });
        if (file is null)
        {
            return;
        }

        try
        {
            var bytes = await bytesTask;
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            vm.Status = $"Exported to {file.Path.LocalPath}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not export: {ex.Message}";
        }
    });

    internal void OnImport(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import archive",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Archive (.zip)") { Patterns = ["*.zip"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        var options = await new ImportOptionsDialog(files[0].Name, vm.CurrentFolderName).ShowDialog<ImportOptionsDialog.Result?>(this);
        if (options is null)
        {
            return;
        }

        try
        {
            vm.Status = "Importing…";
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var result = await vm.ImportAndReloadAsync(buffer.ToArray(), options.UpdateExisting, options.IncludePermissions, options.Merge, options.LeafConflict);
            vm.Status = result is null
                ? "Not signed in."
                : $"Imported \"{result.RootName}\" ({result.Documents} documents, {result.Versions} versions{(result.Skipped > 0 ? $", {result.Skipped} already imported" : "")}).";
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not import: {ex.Message}";
        }
    });

    // Opens the approval workflow for the selected document in a separate window (ADR "Workflow start on
    // demand") — from the ribbon button or the row context menu. Refreshes the Tasks badge afterwards.
    internal void OnStartWorkflow(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.CreateWorkflowViewModel() is not { } workflow)
        {
            return;
        }

        await workflow.LoadAsync();
        await new WorkflowWindow { DataContext = workflow }.ShowDialog(this);
        await vm.ReloadTasksAsync();
    });

    // A transition pressed in the detail pane's workflow slot (#691). Split by what the transition NEEDS:
    // approve/submit/release act; reject needs a reason and reassign a reviewer, so those open the window that
    // can ask. The web client splits them identically (ADR 0511).
    internal void OnWorkflowTransition(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || (sender as Control)?.Tag is not string rel
            || vm.WorkflowTransitions.FirstOrDefault(t => t.Rel == rel) is not { } transition)
        {
            return;
        }

        if (rel is "reject" or "reassign")
        {
            OnStartWorkflow(sender, e);
            return;
        }

        await vm.PerformWorkflowTransitionAsync(transition.Href);
    });

    // The Tenant tab's two administration dialogs — bodies in TenantDialogs, which is where they belong and
    // what keeps this file shrinking rather than growing by one open-a-dialog block per feature (ADR 0558).
    internal void OnManageMailDomains(object? sender, RoutedEventArgs e) =>
        Safe.Fire(() => TenantDialogs.OpenMailDomainsAsync(this, DataContext as MainWindowViewModel));

    internal void OnManageSensitivityLabels(object? sender, RoutedEventArgs e) =>
        Safe.Fire(() => TenantDialogs.OpenSensitivityLabelsAsync(this, DataContext as MainWindowViewModel));

    private async Task RenameSelectedAsync(MainWindowViewModel vm)
    {
        if (vm.SelectedItem is not { } node)
        {
            return;
        }

        var newName = await new RenameDialog(node.Name).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(newName) && newName != node.Name)
        {
            await vm.RenameNodeAsync(node, newName);
        }
    }

    private async Task DeleteSelectedAsync(MainWindowViewModel vm)
    {
        if (vm.SelectedItem is not { } node)
        {
            return;
        }

        var message = (node.IsReference, node.IsFolder) switch
        {
            (true, _) => $"Remove the reference to '{node.Name}' from this folder? The item itself is not deleted.",
            (false, true) => $"Delete the folder '{node.Name}' and everything inside it? It will be moved to the recycle bin.",
            (false, false) => $"Delete '{node.Name}'? It will be moved to the recycle bin.",
        };
        var confirmLabel = node.IsReference ? "Remove" : "Delete";
        if (await new ConfirmDialog(message, confirmLabel).ShowDialog<bool>(this))
        {
            await vm.DeleteNodeAsync(node);
        }
    }

    // Bulk actions on the multi-selection (ADR "Bulk actions on selected documents"). The list is
    // SelectionMode=Multiple; this pushes the current selection to the VM so the bulk bar appears at ≥2.
    internal void OnContentsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ListBox lb)
        {
            vm.SetBulkSelection(lb.SelectedItems?.OfType<NodeViewModel>() ?? []);
        }
    }

    internal void OnBulkMove(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.CreateMoveTargetPickerViewModel() is not { } picker)
        {
            return;
        }

        if (await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this) is { } result)
        {
            await vm.BulkMoveAsync(result.TargetId);
        }
    });

    internal void OnBulkDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm
            && await new ConfirmDialog($"Move {vm.BulkSelectionCount} item(s) to the recycle bin?", "Delete").ShowDialog<bool>(this))
        {
            await vm.BulkDeleteAsync();
        }
    });

    internal void OnBulkAddTags(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var catalog = await vm.GetTagCatalogAsync();
        if (await new BulkTagsDialog(catalog).ShowDialog<IReadOnlyList<string>?>(this) is { Count: > 0 } tags)
        {
            await vm.BulkAddTagsAsync(tags);
        }
    });

    internal void OnBulkSensitivity(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm
            && await new BulkSensitivityDialog(vm.SensitivityPickerItems).ShowDialog<MainWindowViewModel.SensitivityPickerItem?>(this) is { } label)
        {
            await vm.BulkSetSensitivityAsync(label.Id);
        }
    });

    // Right-clicking a row selects it first, so the context menu (and ribbon) act on the row under the cursor.
    internal void OnContentsContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control { DataContext: NodeViewModel node } && DataContext is MainWindowViewModel vm)
        {
            vm.SelectedItem = node;
        }
    }

    // Window-level chords. Tunnelling, so they fire regardless of which pane has focus — neither is a chord that
    // produces text, so taking it out of a focused TextBox costs the user nothing.
    //   Ctrl/Cmd+P → the server manager (ADR "Desktop server configuration")
    //   Ctrl/Cmd+O → open the selected document natively (#482, ADR "One shortcut for opening a document")
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!command)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.P:
                e.Handled = true;
                _ = new ServerManagerWindow().ShowDialog(this);
                break;

            // Handled unconditionally, not only when something is selected: the browser-style alternative — let
            // it fall through when there is no selection — would mean the chord sometimes reaches whatever has
            // focus, which is a worse surprise than a keystroke that does nothing.
            case Key.O:
                e.Handled = true;
                if (DataContext is MainWindowViewModel vm)
                {
                    Safe.Fire(() => vm.OpenSelectedCommand.ExecuteAsync(null));
                }

                break;
        }
    }

    internal void OnListKeyDown(object? sender, KeyEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedItem is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;
                await RenameSelectedAsync(vm);
                break;
            case Key.Delete:
                e.Handled = true;
                await DeleteSelectedAsync(vm);
                break;
            case Key.Escape:
                // Deselect: the detail pane falls back to the folder being stood in. The view-model refuses
                // while editing, where Esc already means "cancel the edit" (ADR 0550).
                e.Handled = true;
                vm.ClearListSelectionCommand.Execute(null);
                break;
        }
    });

    // A click on the list's EMPTY area deselects. Without it a selection could be made and never unmade — the
    // pane could only move from one subject to another. Tunnel phase so it is seen before the ListBox turns the
    // press into a selection, and only when the press landed outside any row.
    internal void OnContentsBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm
            || e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>() is not null)
        {
            return;
        }

        vm.ClearListSelectionCommand.Execute(null);
    }

    // Enter in the search box runs the search.
    // Double-click a row: same as the Open command (folder navigates in, document — or a zip entry — opens
    // natively). A zip entry opens natively too (OpenAsync's IsArchiveEntry branch); Save-as stays on the
    // context menu.
    internal void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }


    // Save one archive entry: pick a destination, download the entry's bytes (Api-proxied), write them.
    private async Task SaveArchiveEntryAsync(MainWindowViewModel vm, SimplArchive.DesktopClient.ViewModels.NodeViewModel entry)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = System.IO.Path.GetFileName(entry.ArchiveEntryPath ?? entry.Name),
        });
        if (file is null)
        {
            return; // cancelled
        }

        try
        {
            var bytes = await vm.DownloadArchiveEntryAsync(entry);
            if (bytes is null)
            {
                vm.Status = $"Could not download '{entry.Name}'.";
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            vm.Status = $"Saved '{System.IO.Path.GetFileName(entry.ArchiveEntryPath ?? entry.Name)}' to {file.Path.LocalPath}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not save: {ex.Message}";
        }
    }













    // Walks up the visual tree from a routed-event source to the nearest DataContext of type T.
    internal static T? FindDataContext<T>(object? source) where T : class
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: T match })
            {
                return match;
            }
        }

        return null;
    }
}
