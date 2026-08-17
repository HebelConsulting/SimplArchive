using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The document/session handlers of the workbench window (issue #466 split the code-behind by feature family):
// legal holds, versions + comparisons, manage access, save-as, references, the intray row actions, check-out,
// recycle bin, and the WebDAV mount buttons. Same class — view-glue whose logic lives in the view models.
public partial class MainWindow
{
    // Place a legal hold on the selected contents-list item — creates a new matter covering it.
    private void OnPlaceLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedItem is not { } node || node.IsReference)
        {
            return;
        }

        if (await new LegalHoldDialog(node.Name).ShowDialog<LegalHoldDialog.Result?>(this) is { } result)
        {
            await vm.CreateLegalHoldAsync(result.Name, result.Reason, node.Id);
        }
    });

    internal void OnNewLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new LegalHoldDialog().ShowDialog<LegalHoldDialog.Result?>(this) is { } result)
        {
            await vm.CreateLegalHoldAsync(result.Name, result.Reason, null);
        }
    });

    internal void OnReleaseLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedLegalHold is not { } hold)
        {
            return;
        }

        var message = $"Release '{hold.Name}'? Its documents are unfrozen unless another active hold still covers them.";
        if (await new ConfirmDialog(message, "Release").ShowDialog<bool>(this))
        {
            await vm.ReleaseSelectedHoldAsync();
        }
    });

    // Double-click on a held document = Go to (the search-results gesture); single click already selected it.
    private void OnGoToHoldItem(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.GoToSelectedHoldItemCommand.Execute(null);
        }
    }

    // Save as…: pick a destination via the native save-file dialog, then download the document's bytes there.
    // Triggered from both the ribbon button and the row context menu.
    // Compare two versions of the selected document (ADR "Document version comparison") — an inline diff dialog
    // (plus an optional Beyond Compare launch when installed).
    // Versions dialog (ADR "Versions dialog") — list versions with Open/Save-as/Make-current + a Compare launcher.
    private void OnVersions(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api || vm.SelectedItem is not { IsFolder: false } node)
        {
            return;
        }

        var vvm = new VersionsViewModel();
        await vvm.SetupAsync(api, node.Id, node.Name, node.Href("versions"));
        var dialog = new VersionsDialog(vvm);
        vvm.RequestClose = dialog.Close;
        await dialog.ShowDialog(this);
        if (vvm.Changed)
        {
            await vm.ReloadSelectedDetailAsync();
        }
    });

    private void OnCompareVersions(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api || vm.SelectedItem is not { IsFolder: false } node)
        {
            return;
        }

        var cvm = new CompareVersionsViewModel();
        await cvm.SetupAsync(api, node.Id, node.Name, node.Href("versions"));
        var dialog = new CompareVersionsDialog(cvm);
        await dialog.ShowDialog(this); // compare is read-only now — "Make current" lives on the Versions dialog (#265)
    });

    // Compare a checked-out document's working copy against its current version (ADR 0517) — inline unified diff +
    // an optional Beyond Compare launch. Shown only on modified rows (the row's Tag carries the CheckoutRowViewModel).
    // The Check-out twin of IntrayItemFrom (#521): a context menu hands over its own row as the Tag, a ribbon
    // button hands over nothing and means the SELECTION. One expression, so the two surfaces cannot drift into
    // disagreeing about which document they act on — and neither consults the detail pane (ADR 0559).
    private static CheckoutRowViewModel? CheckoutRowFrom(object? sender, MainWindowViewModel vm) =>
        (sender as Control)?.Tag as CheckoutRowViewModel ?? vm.Checkout.SelectedRow;

    // Rotate/Sort the WORKING COPY (ADR 0593): the intray recipe against the check-out's pages resource — the
    // rels are re-read at click time so the dialog opens on what the resource says NOW, and one request writes
    // the whole arrangement into the stash. The archive changes only through a normal check-in.
    internal void OnCheckoutSortPages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api
            || CheckoutRowFrom(sender, vm) is not { } row || row.Item is not { } item
            || item.Href("pages") is not { } pagesHref
            || await api.Intray.GetAsync(pagesHref) is not { SortHref: { } sortHref } pages)
        {
            return;
        }

        var thumbnails = await IntrayPageThumbnails.LoadForCheckoutAsync(item, pages.PageCount);
        var dialog = new SortPagesDialog(row.DisplayName, thumbnails);
        if (await dialog.ShowDialog<SortPagesDialog.Result?>(this) is not { } arrangement)
        {
            return;
        }

        try
        {
            await api.Intray.SortAsync(sortHref, arrangement.Order, arrangement.Rotations.Count > 0 ? arrangement.Rotations : null);
            vm.Status = string.Format(Strings.Get("StIntraySorted"), row.DisplayName);
        }
        catch (ApiActionException ex)
        {
            vm.Status = ex.Message;
        }

        await vm.Checkout.LoadAsync();
    });

    internal void OnCheckoutCompare(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api ||
            CheckoutRowFrom(sender, vm) is not CheckoutRowViewModel { Item: { } checkout } row)
        {
            return;
        }

        var ccvm = new CompareCheckoutViewModel();
        await ccvm.SetupAsync(api, checkout, row.DisplayName, row.FileExtension, row.StashDownloadUrl);
        await new CompareCheckoutDialog(ccvm).ShowDialog(this);
    });

    // "Beyond Compare …" straight from the row — the same comparison the dialog offers, without opening the
    // dialog first to reach it. A user who works this way wants the external tool, not the inline diff, and
    // making them pass through the diff to get to it is a step that exists only because of how it was built.
    internal void OnCheckoutBeyondCompare(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel { Api: { } api } vm ||
            CheckoutRowFrom(sender, vm) is not CheckoutRowViewModel row)
        {
            return;
        }

        vm.Status = Strings.Get("StOpeningBc");
        vm.Status = await CheckoutDiffLauncher.OpenAsync(api, row.Item?.DownloadUrl, row.FileExtension, row.StashDownloadUrl);
    });

    private void OnManageAccess(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api || vm.SelectedItem is not { } node)
        {
            return;
        }

        var mvm = new ManageAccessViewModel();
        await mvm.SetupAsync(api, node.DocumentSelfHref, node.Name);
        await new ManageAccessDialog(mvm).ShowDialog(this);
    });

    private void OnSaveAs(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedItem is not { IsFolder: false } node)
        {
            return;
        }

        if (node.IsArchiveEntry)
        {
            await SaveArchiveEntryAsync(vm, node);
            return;
        }

        var (url, suggestedFileName) = await vm.GetDownloadInfoAsync(node);
        if (url is null)
        {
            vm.Status = $"'{node.Name}' has no downloadable version.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = suggestedFileName,
        });
        if (file is null)
        {
            return; // cancelled
        }

        try
        {
            var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(url);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            vm.Status = $"Saved '{node.Name}' to {file.Path.LocalPath}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not save '{node.Name}': {ex.Message}";
        }
    });

    // References … (context menu, items with references): list the folders that reference this item; opening
    // a row navigates to that folder.
    private void OnReferences(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.CreateReferencesViewModel() is not { } references)
        {
            return;
        }

        await references.LoadAsync();
        var result = await new ReferencesDialog { DataContext = references }.ShowDialog<ReferencesDialogResult?>(this);
        if (result is not { } r)
        {
            return;
        }

        if (r.Promote)
        {
            await vm.PromotePrimaryLocationAsync(references.DocumentSelfHref, references.ItemId, r.FolderId,
                    r.FolderHref ?? throw new InvalidOperationException("The referencing-folder row advertised no 'open' rel (ADR 0543/0555)."));
        }
        else
        {
            // Open the chosen folder AND select the item for viewing — its real row in the primary location, or its
            // reference (shortcut) row in a referencing folder.
            await vm.OpenFolderAsync(
                r.FolderHref ?? throw new InvalidOperationException("The referencing-folder row advertised no 'open' rel (ADR 0543/0555)."),
                references.ItemId);
        }
    });

    // Go to … (context menu, references only): jump to the referenced item's real home folder.
    private void OnGoTo(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedItem is { IsReference: true } node)
        {
            await vm.GoToReferenceAsync(node);
        }
    });

    private void OnIntrayItemDoubleTapped(object? sender, TappedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.OpenServerIntrayItemCommand.ExecuteAsync(null);
        }
    });

    internal void OnIntrayOpen(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && IntrayItemFrom(sender) is { } item)
        {
            vm.SelectedServerIntrayItem = item;
            await vm.OpenServerIntrayItemCommand.ExecuteAsync(null);
        }
    });

    // File a server-intray item into a folder chosen from the picker.
    internal void OnIntrayFile(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || IntrayItemFrom(sender) is not { } item ||
            vm.CreateFolderPickerViewModel() is not { } picker)
        {
            return;
        }

        await picker.LoadAsync();
        var result = await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this);
        if (result is null)
        {
            return;
        }

        if (result.Mode == FilingMode.AsVersion)
        {
            await vm.FileServerIntrayItemAsVersionAsync(item, result.TargetId, result.Comment);
        }
        else
        {
            await vm.FileServerIntrayItemAsync(item, result.TargetId, result.Comment);
        }
    });

    // "File multiple items": bulk-file the selected server intray items into one folder (ADR "Bulk-file multiple
    // inbox items").
    internal void OnIntrayFileMultiple(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var items = ServerIntrayList.SelectedItems?.OfType<IntrayItemViewModel>().ToList() ?? [];
        if (items.Count == 0 || vm.CreateBulkFolderPickerViewModel() is not { } picker)
        {
            return;
        }

        await picker.LoadAsync();
        var result = await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this);
        if (result is not null)
        {
            await vm.FileMultipleServerItemsAsync(items, result.TargetId, result.Comment);
        }
    });

    // The Check-out selection, kept on the view-model so ribbon gates + bulk actions see how many rows are
    // highlighted — SelectedRow alone cannot say (#521, the multi-select piece).
    private void OnCheckoutSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Checkout.SetSelection(CheckoutList.SelectedItems?.OfType<CheckoutRowViewModel>().ToList() ?? []);
        }
    }

    // A row's context menu hands over its own row as the Tag and means THAT row; a ribbon button hands over
    // nothing and means the whole selection — the same two scopes CheckoutRowFrom resolves for the single-row
    // actions, extended to a list for the verbs that compose (#521).
    private IReadOnlyList<CheckoutRowViewModel> CheckoutRowsFrom(object? sender, MainWindowViewModel vm) =>
        (sender as Control)?.Tag is CheckoutRowViewModel tagged ? [tagged]
        : vm.Checkout.Selection.Count > 0 ? vm.Checkout.Selection
        : vm.Checkout.SelectedRow is { } single ? [single]
        : [];

    // Check in the selection — one document or many; the view-model routes a single row through the single-row
    // path so its wording stays what it was.
    internal void OnCheckoutCheckIn(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.Checkout.CheckInSelectionAsync(CheckoutRowsFrom(sender, vm));
        }
    });

    // Discard a checked-out document's changes (ADR "Document check-out / check-in"; ADR 0513) — confirmed, since it
    // abandons the working copy in check-out and releases the lock without creating a new version. For a
    // multi-selection the confirmation names the COUNT, because "are you sure?" without a scope invites a yes
    // to a question the user did not read.
    internal void OnCheckoutDiscard(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var rows = CheckoutRowsFrom(sender, vm);
        if (rows.Count > 1)
        {
            var eligible = rows.Count(r => r.CanDiscard);
            if (await new ConfirmDialog(string.Format(Strings.Get("CoBulkDiscardConfirm"), eligible), "Discard").ShowDialog<bool>(this))
            {
                await vm.Checkout.DiscardSelectionAsync(rows);
            }

            return;
        }

        if (rows.FirstOrDefault() is { } row
            && await new ConfirmDialog($"Discard the changes to '{row.Name}' and release the check-out?", "Discard").ShowDialog<bool>(this))
        {
            await vm.Checkout.DiscardAsync(row);
        }
    });

    // The Recycle bin selection, kept on the view-model so the ribbon's bulk buttons see how many rows are
    // highlighted (#530 tranche 1 — the Check-out recipe).
    private void OnRecycleBinSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RecycleBin.SetSelection(RecycleBinList.SelectedItems?.OfType<RecycleBinRowViewModel>().ToList() ?? []);
        }
    }

    // Restore / hard-delete ONE recycle-bin row, addressed from the row's context menu via its Tag (#530
    // tranche 1 — the CheckoutRow recipe; the ribbon's selection-scoped twins arrive with the ribbon).
    internal void OnRecycleBinRestore(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && (sender as Control)?.Tag is RecycleBinRowViewModel row)
        {
            await vm.RecycleBin.RestoreCommand.ExecuteAsync(row);
        }
    });

    internal void OnRecycleBinHardDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && (sender as Control)?.Tag is RecycleBinRowViewModel row)
        {
            await vm.RecycleBin.HardDeleteCommand.ExecuteAsync(row);
        }
    });

    // Empty the whole recycle bin — gated behind the "I AGREE" confirmation dialog (ADR "Desktop recycle bin
    // parity"). The per-row hard-delete needs no confirmation; this one is destructive across everything.
    internal void OnRecycleBinHardDeleteAll(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new HardDeleteAllDialog().ShowDialog<bool>(this))
        {
            await vm.RecycleBin.HardDeleteAllAsync();
        }
    });

    // Bulk purge of the checked items (ADR "Bulk purge of selected recycle-bin items") — same "I AGREE" gate.
    internal void OnRecycleBinPurgeSelected(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new HardDeleteAllDialog().ShowDialog<bool>(this))
        {
            await vm.RecycleBin.PurgeSelectedAsync();
        }
    });

    // ONE ribbon button, three states (#461): set up credentials, mount, or open what is already mounted.
    //
    // The order matters and is not arbitrary. Already-mounted is checked FIRST and answered locally, so the
    // common case — "show me my documents" — costs no request at all. Only then does it ask the server whether
    // credentials exist, because that is the only question the client cannot answer itself.
    private void OnWebDavRibbon(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel { Api: { } api } vm)
        {
            return;
        }

        // The Repositories tab's folder is its context, exactly as Personal/Intray is the Intray tab's — the
        // mounted volume IS the tree-pane (ADR 0509). With nothing selected the path is empty and the whole
        // archive opens: the button still does what it says, rather than nothing.
        await OpenWebDavAtAsync(vm, api, vm.WebDavFolderPath());
    });

    internal void OnManageWebDav(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new WebDavDialog(api).ShowDialog(this);
        }
    });

    // The Intray / Check-out tabs' single WebDAV button (ADR "One WebDAV button per tab, deep-linked"). It does
    // the same next-useful-thing the ribbon button does — set up credentials, else mount, else open what is
    // already mounted — with one difference that is the whole point of it being on a tab: when the volume is
    // ALREADY mounted it opens that tab's own folder directly, not the mount root. The user pressed a button on
    // the Intray tab; landing them in the archive root and making them navigate is answering a question they did
    // not ask.
    //
    // The button's Tag names the folder within the single mount ("Personal/Intray", "Personal/Check-out").
    internal void OnWebDavTabButton(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel { Api: { } api } vm)
        {
            return;
        }

        await OpenWebDavAtAsync(vm, api, ((sender as Control)?.Tag as string ?? string.Empty).Trim('/'));
    });

    // Set up credentials, else mount, else open — landing in `subFolder` within the one mount. Shared by the
    // ribbon and both tab buttons so "what does this button do next" is answered in one place; only the folder
    // differs, and that is the tab's own context.
    private async Task OpenWebDavAtAsync(MainWindowViewModel vm, Services.SimplArchiveApiClient api, string subFolder)
    {
        // Already mounted: no server round trip and no re-mount — go straight to the folder on disk.
        if (OsFileManager.MountedPath() is { } mounted)
        {
            vm.Status = Strings.Get("MwWebDavOpening");
            var target = subFolder.Length == 0
                ? mounted
                : System.IO.Path.Combine(mounted, System.IO.Path.Combine(subFolder.Split('/')));
            var opened = await OsFileManager.OpenLocalFolderAsync(target);

            // Always report the outcome. The ribbon used to discard this result, so a failure left the status
            // line reading "Opening SimplArchive …" for ever — which is how a dead button looks from outside.
            vm.Status = opened.Success
                ? Strings.Get("MwWebDavMounted")
                : string.Format(Strings.Get("MwWebDavOpenFailed"), opened.Error);
            return;
        }

        var status = await api.Profile.GetWebDavStatusAsync();
        if (!status.Enabled)
        {
            // No credentials yet: the dialog IS the next useful thing, not an error about the missing ones.
            await new WebDavDialog(api).ShowDialog(this);
            await vm.RefreshWebDavStateAsync();
            return;
        }

        vm.Status = Strings.Get("MwWebDavMounting");
        var result = await OsFileManager.OpenWebDavFolderAsync(status.Url.TrimEnd('/'), subFolder);
        vm.Status = result.Success
            ? Strings.Get("MwWebDavMounted")
            : string.Format(Strings.Get("MwWebDavMountFailed"), result.Error);
        await vm.RefreshWebDavStateAsync();
    }

    // Context-menu twins of the Search toolbar's document group (#530 tranche 8), addressed from the row the
    // menu was opened from via its DataContext (ADR 0559): Preview makes the row THE selection — the selection
    // is what loads the pane — and Go to jumps to the document's location on the Repositories tab.
    internal void OnSearchResultPreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && (sender as Control)?.DataContext is SearchResultViewModel row)
        {
            vm.SelectedSearchResult = row;
        }
    }

    internal void OnSearchResultGoTo(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && (sender as Control)?.DataContext is SearchResultViewModel row)
        {
            await vm.OpenSearchResultAsync(row);
        }
    });
}
