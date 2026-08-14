using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
        // (issue #466); it wires its own handlers onto the list, tree and inbox controls.
        new WorkbenchDragDrop(this, ContentsList, FolderTree, ServerInboxList).Wire();

        // Ctrl/Cmd+P opens the server manager (ADR "Desktop server configuration"), Ctrl/Cmd+O opens the selected
        // document (#482) — a window-level tunnel handler so they fire regardless of focus.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Advertise the Open chord in the menu entry itself — a shortcut nobody can discover is one nobody uses.
        // Set here rather than in XAML because it is ⌘ on macOS and Ctrl elsewhere (Services.Shortcuts).
        OpenMenuItem.InputGesture = Shortcuts.Open;

        // Tapping a tree folder always shows its contents — even the already-selected node, so re-clicking the
        // tree re-syncs the list after drilling into a subfolder via the contents pane (the binding alone
        // short-circuits a same-node re-selection). See MainWindowViewModel.ReselectTreeFolderAsync.
        FolderTree.AddHandler(Gestures.TappedEvent, OnTreeItemTapped);

        // Provide the sticky-note dialog to the Repositories/Inbox preview (ADR "Document annotations"). Set on
        // the main Preview only, so the Recycle-bin preview never offers note editing. Kept in code-behind since
        // it opens an Avalonia Window owned by this window, keeping the VM view-agnostic (mirrors StatusReporter).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Preview.AnnotationDialog = ShowAnnotationDialogAsync;
                vm.ExtendRetentionDialog = name => new ExtendRetentionDialog(name).ShowDialog<string?>(this);
                vm.SaveSearchNamePrompt = () => new NewFolderDialog("Save search", "Name this saved search").ShowDialog<string?>(this);
                vm.DuplicateUploadDialog = req => new DuplicateUploadDialog(req).ShowDialog<MainWindowViewModel.DuplicatePromptResult?>(this);
                vm.NameConflictDialog = req => new NameConflictDialog(req).ShowDialog<Services.UploadConflictResolver.NameConflictChoice?>(this);
                vm.ShowReminderDialog = rvm => new ReminderDialog(rvm).ShowDialog(this);
                vm.ShowExternalLinksDialog = evm =>
                {
                    var window = new ExternalLinksDialog(evm);
                    // The view-model closes its own window for "Go to": the navigation it triggers happens in the
                    // workbench behind, which a dialog left open would be covering.
                    evm.RequestClose = window.Close;
                    return window.ShowDialog(this);
                };
                vm.ShowExternalLinkDetailDialog = dvm => new ExternalLinkDetailDialog(dvm).ShowDialog(this);
                vm.ShowShareSavedSearchDialog = svm => new ShareSavedSearchDialog(svm).ShowDialog<bool>(this);
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
    private void OnNewFolder(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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
    private void OnRename(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await RenameSelectedAsync(vm);
        }
    });

    private void OnDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await DeleteSelectedAsync(vm);
        }
    });














    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") ----------------------------









    // Help ▸ Manual (ADR 0504): open the auto-generated user manual (served at /download/manual/ on the
    // connected server, ADR 0502) in the system browser rather than embedding a PDF viewer.
    private void OnOpenManual(object? sender, RoutedEventArgs e) =>
        SystemBrowser.Open($"{DesktopClientOptions.ApiBaseUrl}/download/manual/SimplArchive-Manual.pdf");

    // Help ▸ About (ADR 0504): the vendor block + the running client version.
    private void OnShowAbout(object? sender, RoutedEventArgs e) =>
        Safe.Fire(async () => await new AboutDialog().ShowDialog(this));




    // ---- Legal holds (ADR "Legal hold & retention enforcement") ------------------------------------





    // Open (context menu): same as the ribbon Open button / double-click — a folder (or a document with
    // children, e.g. an email with attachments) drills in, a plain document opens in its native application.
    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }









    // ---- Inbox (ADR "S3-backed inbox", phase 2) -------------------------------------------------------

    private static InboxItemViewModel? InboxItemFrom(object? sender) =>
        (sender as Control)?.Tag as InboxItemViewModel;





    // Track the server-inbox selection count so the "File multiple items" button shows only for 2+.
    private void OnServerInboxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var selected = ServerInboxList.SelectedItems?.Count ?? 0;
        vm.CanFileMultiple = selected >= 2;
        vm.InboxActions.SelectedCount = selected;

        // Ask what THIS row's pages can do. Fire-and-forget through Safe.Fire because a selection change must
        // not wait on a request; the buttons are cleared first, so during the flight they say "not available",
        // which is exactly true (ADR 0559).
        Safe.Fire(async () => await vm.InboxActions.LoadPagesAsync(
            selected == 1 ? ServerInboxList.SelectedItem as InboxItemViewModel : null));
    }

    // ---- Page operations (#487, ADR 0575) -------------------------------------------------------------
    // Each is addressed from the href the server advertised for the SELECTED row, never composed (ADR 0543),
    // and each re-reads that address at the moment of acting rather than trusting pane state (ADR 0559).

    private void OnInboxSplit(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || ServerInboxList.SelectedItem is not InboxItemViewModel item
            || await vm.InboxActions.GetPagesAsync(item) is not { SplitHref: { } splitHref } pages)
        {
            return;
        }

        // Splitting adds N items and keeps the source, so the count is worth stating before it happens: on a
        // 40-page scan the difference between "split" and "what have I done" is knowing it was 40.
        var prompt = string.Format(Strings.Get("InboxSplitConfirm"), item.Name, pages.PageCount);
        if (await new ConfirmDialog(prompt, Strings.Get("InboxSplit")).ShowDialog<bool>(this))
        {
            await vm.InboxActions.SplitAsync(item, splitHref);
        }
    });

    private void OnInboxSortPages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || ServerInboxList.SelectedItem is not InboxItemViewModel item
            || item.Item is not { } info
            || vm.Api is not { } api
            || await vm.InboxActions.GetPagesAsync(item) is not { SortHref: { } sortHref })
        {
            return;
        }

        var thumbnails = await InboxPageThumbnails.LoadAsync(api, info);
        var dialog = new SortPagesDialog(item.Name, thumbnails.Cast<Bitmap?>().ToList());
        if (await dialog.ShowDialog<IReadOnlyList<int>?>(this) is { } order)
        {
            await vm.InboxActions.SortAsync(item, sortHref, order);
        }
    });

    private void OnInboxJoin(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var names = (ServerInboxList.SelectedItems ?? new List<object>())
            .OfType<InboxItemViewModel>().Select(i => i.Name).ToList();
        if (names.Count < 2)
        {
            return;
        }

        if (await new JoinItemsDialog(names).ShowDialog<JoinItemsDialog.Result?>(this) is { } result)
        {
            await vm.InboxActions.JoinAsync(result.Names, result.Name);
        }
    });



    private void OnInboxDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || InboxItemFrom(sender) is not { } item)
        {
            return;
        }

        if (await new ConfirmDialog($"Delete '{item.Name}' from the inbox?", "Delete").ShowDialog<bool>(this))
        {
            await vm.InboxActions.DeleteAsync(item);
        }
    });

    // "Send to…" (ADR 0532): hand an own item to a chosen group or user via the picker dialog.
    private void OnInboxSend(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || InboxItemFrom(sender) is not { } item)
        {
            return;
        }

        var targets = await vm.InboxActions.GetSendTargetsAsync();
        if (await new SendToInboxDialog(item.Name, targets).ShowDialog<SimplArchiveApiClient.InboxTargetInfo?>(this) is { } target)
        {
            await vm.InboxActions.SendAsync(item, target);
        }
    });

    // "Move to my inbox" (ADR 0532): claim a group / other-user item into my own inbox.
    private void OnInboxMoveToMine(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && InboxItemFrom(sender) is { } item)
        {
            await vm.InboxActions.ClaimToMineAsync(item);
        }
    });

    // Edit the OCR languages (system field): the ordered multi-select picker (ADR "System fields +
    // OCR-language mask field").
    private void OnEditOcrLanguages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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

    // Inbox mask pane's OCR-language picker (ADR "Inbox OCR-language staging") — mirrors OnEditOcrLanguages.
    private void OnEditInboxOcrLanguages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var (catalog, selected) = vm.InboxOcrPickerState();
        if (catalog.Count == 0)
        {
            return;
        }

        var picker = new OcrLanguagePickerViewModel(catalog, selected);
        var codes = await new OcrLanguagePickerDialog { DataContext = picker }.ShowDialog<List<string>?>(this);
        if (codes is not null)
        {
            vm.StageInboxOcrLanguages(codes); // staged into the pane; the pane's Save persists it
        }
    });

    // Tenant-admin tab: New repository — prompt for a name, then create a root-level document (ADR "Tenant-admin
    // settings tab"). Reuses the name-prompt dialog with a repository-specific title/label.
    private void OnNewRepository(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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
    private void OnEditTenantOcrLanguages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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
    private void OnConvertExistingTiffs(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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
    private void OnExport(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var options = await new ExportDialog(vm.ExportRootName).ShowDialog<SimplArchiveApiClient.RepositoryExportOptions?>(this);
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

    private void OnImport(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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

    // Export the audit log (Audit tab) as NDJSON to a chosen file (ADR "Audit trail export").
    private void OnAuditExport(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export audit log",
            SuggestedFileName = $"audit-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.ndjson",
        });
        if (file is null || vm.ExportAuditBytesAsync() is not { } bytesTask)
        {
            return;
        }

        try
        {
            var bytes = await bytesTask;
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            vm.Status = $"Exported the audit log to {file.Path.LocalPath}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not export the audit log: {ex.Message}";
        }
    });

    // Purge aged audit events (tenant-admin, Audit tab): confirm, then run the purge (ADR "Desktop audit
    // viewer" over "Audit trail retention and purge").
    private void OnAuditPurge(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var message = $"Permanently delete audit events older than {(vm.AuditRetentionDays == 0 ? "— (retention disabled)" : $"{vm.AuditRetentionDays} days")}? The tamper-evidence chain stays verifiable over the retained events.";
        if (await new ConfirmDialog(message, "Purge").ShowDialog<bool>(this))
        {
            await vm.PurgeAuditAsync();
        }
    });

    // Opens the approval workflow for the selected document in a separate window (ADR "Workflow start on
    // demand") — from the ribbon button or the row context menu. Refreshes the Tasks badge afterwards.
    private void OnStartWorkflow(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.CreateWorkflowViewModel() is not { } workflow)
        {
            return;
        }

        await workflow.LoadAsync();
        await new WorkflowWindow { DataContext = workflow }.ShowDialog(this);
        await vm.ReloadTasksAsync();
    });

    private void OnManageSensitivityLabels(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.CreateSensitivityLabelsViewModel() is not { } labels)
        {
            return;
        }

        await labels.LoadAsync();
        await new SensitivityLabelsDialog(labels).ShowDialog(this);
        await vm.LoadSensitivityCatalogAsync(); // pick up any changes for the picker
    });

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
    private void OnContentsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ListBox lb)
        {
            vm.SetBulkSelection(lb.SelectedItems?.OfType<NodeViewModel>() ?? []);
        }
    }

    // Dragging a contents-list header column's right-edge Thumb resizes that column (ADR "Desktop list-pane
    // resizable columns"); the Thumb's Tag carries the 0-based column index. Persisted on drag completion.
    private void OnColumnResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { Tag: { } tag }
            && int.TryParse(tag.ToString(), out var index))
        {
            vm.ResizeColumn(index, e.Vector.X);
        }
    }

    private void OnColumnResizeDone(object? sender, VectorEventArgs e) => (DataContext as MainWindowViewModel)?.SaveLayout();

    private void OnBulkMove(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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

    private void OnBulkDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm
            && await new ConfirmDialog($"Move {vm.BulkSelectionCount} item(s) to the recycle bin?", "Delete").ShowDialog<bool>(this))
        {
            await vm.BulkDeleteAsync();
        }
    });

    private void OnBulkAddTags(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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

    private void OnBulkSensitivity(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm
            && await new BulkSensitivityDialog(vm.SensitivityPickerItems).ShowDialog<MainWindowViewModel.SensitivityPickerItem?>(this) is { } label)
        {
            await vm.BulkSetSensitivityAsync(label.Id);
        }
    });

    // Right-clicking a row selects it first, so the context menu (and ribbon) act on the row under the cursor.
    private void OnContentsContextRequested(object? sender, ContextRequestedEventArgs e)
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

    private void OnListKeyDown(object? sender, KeyEventArgs e) => Safe.Fire(async () =>
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
        }
    });

    // Enter in the search box runs the search.
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm && vm.SearchCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.SearchCommand.Execute(null);
        }
    }

    // Double-click a search result: switch to the Repositories tab and navigate to it.
    private void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedSearchResult is { } result)
        {
            await vm.OpenSearchResultAsync(result);
        }
    });

    // Double-click a row: same as the Open command (folder navigates in, document — or a zip entry — opens
    // natively). A zip entry opens natively too (OpenAsync's IsArchiveEntry branch); Save-as stays on the
    // context menu.
    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }

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

    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
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
        }
    }

    private void OnTreeNewFolder(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node)
        {
            return;
        }

        var name = await new NewFolderDialog().ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await vm.CreateSubfolderAsync(node.Id, name);
        }
    });

    private void OnTreeRename(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node)
        {
            return;
        }

        var name = await new RenameDialog(node.Name).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name) && name != node.Name)
        {
            await vm.RenameFolderByIdAsync(node.Id, name);
        }
    });

    private void OnTreeDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && _treeContextNode is { } node
            && await new ConfirmDialog($"Delete the folder '{node.Name}' and everything inside it? It will be moved to the recycle bin.", "Delete").ShowDialog<bool>(this))
        {
            await vm.DeleteFolderByIdAsync(node.Id);
        }
    });

    // ---- The rest of the tree context menu's folder actions (ADR "Tree-pane context menu") ----------------
    // Each targets the RIGHT-CLICKED node (_treeContextNode), not the contents-list SelectedItem — a tree
    // right-click means "this folder". The selection-scoped ones (Export, folder sort) work off the fact that
    // OnTreeContextRequested has already made the node the current selection.

    // Upload files into the right-clicked folder. Reuses the drag-and-drop path (UploadDroppedFilesAsync), so
    // duplicate detection and per-file error reporting behave identically to a drop.
    private void OnTreeUpload(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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
            await vm.UploadDroppedFilesAsync(files, node.Id);
        }
    });

    private void OnTreeMove(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node
            || vm.CreateMoveTargetPickerViewModel() is not { } picker)
        {
            return;
        }

        if (await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this) is { } result)
        {
            await vm.MoveFolderByIdAsync(node.Id, node.Name, result.TargetId);
        }
    });

    // Puts the detail pane's folder-settings row into edit mode for the right-clicked folder (its contents sort
    // order) — the same editor its "Edit" button opens.
    private void OnTreeFolderSort(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.BeginFolderSortEditCommand.Execute(null);
        }
    }

    private void OnTreePlaceLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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
    private void OnTreePlaceReference(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node
            || vm.CreateMoveTargetPickerViewModel() is not { } picker)
        {
            return;
        }

        if (await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this) is { } result)
        {
            await vm.PlaceReferenceAsync(node.Id, node.Name, result.TargetId);
        }
    });

    private void OnTreeReferences(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _treeContextNode is not { } node
            || vm.CreateReferencesViewModel(node.Id, node.Name) is not { } references)
        {
            return;
        }

        await references.LoadAsync();
        var result = await new ReferencesDialog { DataContext = references }.ShowDialog<ReferencesDialogResult?>(this);
        if (result is { } r)
        {
            if (r.Promote)
            {
                await vm.PromotePrimaryLocationAsync(references.ItemId, r.FolderId);
            }
            else
            {
                await vm.OpenFolderAsync(r.FolderId, references.ItemId);
            }
        }
    });

    // Manage access on the right-clicked TREE folder (ADR "Tree-pane context menu with manage-access"). The
    // contents-list menu's OnManageAccess acts on SelectedItem; a tree node isn't a list row, so this targets
    // _treeContextNode instead. The dialog self-gates on the caller's own CanManagePermissions.
    private void OnTreeManageAccess(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api || _treeContextNode is not { } node)
        {
            return;
        }

        var mvm = new ManageAccessViewModel();
        await mvm.SetupAsync(api, node.Id, node.Name);
        await new ManageAccessDialog(mvm).ShowDialog(this);
    });

    private void OnTreeRefresh(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.RefreshCommand.ExecuteAsync(null);
        }
    });

    private void OnTreeToggleFollow(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && _treeContextNode is { IsSynthetic: false } node)
        {
            await vm.ToggleFolderSubscriptionAsync(node.Id);
        }
    });

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
