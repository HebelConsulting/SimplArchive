using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

public partial class MainWindow : Window
{
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

    public MainWindow()
    {
        InitializeComponent();

        // Drag-and-drop upload onto the contents list (uploads into the currently-open folder), and internal
        // move/reference drags of a row onto a folder. The Drop/DragOver routed events are added in
        // code-behind (AllowDrop is set in XAML). See ADR "Desktop drag-and-drop upload" and "… move and
        // reference".
        ContentsList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ContentsList.AddHandler(DragDrop.DropEvent, OnDrop);
        ContentsList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        ContentsList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        ContentsList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);

        // Ctrl/Cmd+P opens the server manager (ADR "Desktop server configuration") — a window-level tunnel
        // handler so it fires regardless of focus.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // The folder tree is a drop target too (drop onto any folder, incl. a repository root) AND a drag source
        // (drag a folder onto another folder to move/reference it). Tunnel pointer handlers mirror the list's, so
        // a plain click still selects/expands and only a past-threshold drag starts a move.
        FolderTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        FolderTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        FolderTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        FolderTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        FolderTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);

        // The inbox file-list is a drop target for OS files — dropping uploads them into the S3-backed inbox
        // (ADR "Inbox file-list drop-zone").
        ServerInboxList.AddHandler(DragDrop.DragOverEvent, OnInboxDragOver);
        ServerInboxList.AddHandler(DragDrop.DropEvent, OnInboxDrop);
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

    // Users & groups admin (ADR "Users & groups administration tab") — the New/Copy dialogs and the Delete
    // confirm live in the view; the VM does the Api work.
    private void OnNewUser(object? sender, RoutedEventArgs e) => Safe.Fire(() => NewPrincipalAsync(false));

    private void OnNewGroup(object? sender, RoutedEventArgs e) => Safe.Fire(() => NewPrincipalAsync(true));

    private async Task NewPrincipalAsync(bool isGroup)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var result = await new PrincipalDialog(isGroup, "", "").ShowDialog<PrincipalDialog.Result?>(this);
        if (result is not null)
        {
            await vm.CreatePrincipalAsync(isGroup, result.Name, result.Email, null);
        }
    }

    // Copy = the New dialog pre-filled from the selection; the created principal gets the source's rights.
    private void OnCopyPrincipal(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { } p)
        {
            return;
        }

        var initialName = p.IsGroup ? $"{p.Name} (copy)" : p.Name;
        var result = await new PrincipalDialog(p.IsGroup, initialName, "").ShowDialog<PrincipalDialog.Result?>(this);
        if (result is not null)
        {
            await vm.CreatePrincipalAsync(p.IsGroup, result.Name, result.Email, p.Rights);
        }
    });

    // Discard a checked-out document's changes (ADR "Document check-out / check-in"; ADR 0513) — confirmed, since it
    // abandons the working copy in check-out and releases the lock without creating a new version.
    private void OnCheckoutDiscard(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: CheckoutRowViewModel row }
            && await new ConfirmDialog($"Discard the changes to '{row.Name}' and release the check-out?", "Discard").ShowDialog<bool>(this))
        {
            await vm.Checkout.DiscardAsync(row);
        }
    });

    // Empty the whole recycle bin — gated behind the "I AGREE" confirmation dialog (ADR "Desktop recycle bin
    // parity"). The per-row hard-delete needs no confirmation; this one is destructive across everything.
    private void OnRecycleBinHardDeleteAll(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new HardDeleteAllDialog().ShowDialog<bool>(this))
        {
            await vm.RecycleBin.HardDeleteAllAsync();
        }
    });

    // Bulk purge of the checked items (ADR "Bulk purge of selected recycle-bin items") — same "I AGREE" gate.
    private void OnRecycleBinPurgeSelected(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new HardDeleteAllDialog().ShowDialog<bool>(this))
        {
            await vm.RecycleBin.PurgeSelectedAsync();
        }
    });

    private void OnDeletePrincipal(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { } p)
        {
            return;
        }

        var message = p.IsGroup ? $"Delete the group '{p.Name}'?" : $"Deactivate the user '{p.Name}'?";
        var confirmLabel = p.IsGroup ? "Delete" : "Deactivate";
        if (!await new ConfirmDialog(message, confirmLabel).ShowDialog<bool>(this))
        {
            return;
        }

        // A user with pending review tasks can't be deactivated without handing them over (ADR "Workflow
        // review reassignment") — prompt for a replacement reviewer and retry.
        if (await vm.DeleteSelectedPrincipalAsync() == MainWindowViewModel.DeletePrincipalOutcome.NeedsReplacementReviewer)
        {
            var candidates = vm.ReplacementReviewerCandidates();
            if (candidates.Count == 0)
            {
                return;
            }

            if (await new ReplacementReviewerDialog(p.Name, candidates).ShowDialog<Guid?>(this) is { } replacementId)
            {
                await vm.ReassignReviewsAndDeactivateAsync(replacementId);
            }
        }
    });

    // Service accounts (machine-to-machine, ADR 0534) — a self-contained manager window that talks to the API
    // via the shared client; gated on CanManageServiceAccounts (the server enforces it on every call too).
    private void OnManageServiceAccounts(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new ServiceAccountsWindow(api).ShowDialog(this);
        }
    });

    // Profile photo (ADR "User profile photo") — the crop dialog lives in the view; the VM uploads.
    private void OnChangeMyPhoto(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new ProfilePhotoDialog().ShowDialog<byte[]?>(this) is { } png)
        {
            await vm.SetMyPhotoAsync(png);
        }
    });

    private void OnChangePrincipalPhoto(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new ProfilePhotoDialog().ShowDialog<byte[]?>(this) is { } png)
        {
            await vm.SetSelectedUserPhotoAsync(png);
        }
    });

    private void OnRemovePrincipalPhoto(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.RemoveSelectedUserPhotoAsync();
        }
    });

    // Passwords (ADR "User password management") — the dialogs live in the view; the VM does the API call.
    private void OnChangeMyPassword(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new ChangePasswordDialog().ShowDialog<ChangePasswordDialog.Result?>(this) is { } result)
        {
            await vm.ChangeMyPasswordAsync(result.Current, result.New);
        }
    });

    private void OnResetPrincipalPassword(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        var message = $"Reset the password for '{p.Name}'? A new random password will be generated and shown once.";
        if (!await new ConfirmDialog(message, "Reset").ShowDialog<bool>(this))
        {
            return;
        }

        if (await vm.ResetSelectedUserPasswordAsync() is { } password)
        {
            await new GeneratedPasswordDialog(p.Name, password).ShowDialog(this);
        }
    });

    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") ----------------------------

    private void OnSetUpMfa(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api } vm && await new MfaSetupDialog(api).ShowDialog<bool>(this))
        {
            vm.MarkMfaEnabled();
        }
    });

    private void OnDisableMfa(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (await new ConfirmDialog("You'll no longer be asked for a code when you sign in. Continue?", "Disable").ShowDialog<bool>(this))
        {
            await vm.DisableMyMfaAsync();
        }
    });

    // Passkeys (ADR "Desktop passkey management") — list/remove natively; adding opens the browser ceremony.
    private void OnManagePasskeys(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new PasskeysDialog(api).ShowDialog(this);
        }
    });

    private void OnManageWebDav(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new WebDavDialog(api).ShowDialog(this);
        }
    });

    // "Open in file manager" (Inbox tab): mount the user's Personal WebDAV folder (Inbox / Check-out / archive)
    // and open it in Finder / Explorer / Files. When WebDAV isn't set up yet, open the settings dialog so the
    // user can set a password there and then, instead of just hinting (ADR "Desktop inbox WebDAV buttons").
    private void OnOpenWebDavMount(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel { Api: { } api } vm)
        {
            return;
        }

        // The button's Tag names a subfolder to deep-open WITHIN the single mount (e.g. "Personal/Check-out"), so
        // the Inbox / Check-out buttons land the user straight in that folder; absent → open the SimplArchive root.
        var subFolder = (sender as Control)?.Tag as string;

        try
        {
            var status = await api.GetWebDavStatusAsync();
            if (!status.Enabled)
            {
                vm.Status = "Set up a WebDAV password to mount your folder.";
                await new WebDavDialog(api).ShowDialog(this);
                return;
            }

            // Mount the single "SimplArchive" resource — the whole tree (Personal, with Inbox/Check-out, + the
            // shared repositories) so the OS volume is named "SimplArchive" (ADR 0509) — then, when a subfolder is
            // given, open straight into it within that one mount.
            var baseUrl = status.Url.TrimEnd('/');
            OsFileManager.OpenResult result;
            if (string.IsNullOrWhiteSpace(subFolder))
            {
                vm.Status = "Opening SimplArchive (your Personal space + repositories) in your file manager…";
                result = await OsFileManager.OpenWebDavAsync(baseUrl);
            }
            else
            {
                vm.Status = $"Opening {subFolder} in your file manager…";
                result = await OsFileManager.OpenWebDavFolderAsync(baseUrl, subFolder);
            }

            if (!result.Success)
            {
                vm.Status = $"Could not open the WebDAV folder: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not open the WebDAV folder: {ex.Message}";
        }
    });

    private void OnNotificationPreferences(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new NotificationPreferencesDialog(api).ShowDialog(this);
        }
    });

    // Help ▸ Manual (ADR 0504): open the auto-generated user manual (served at /download/manual/ on the
    // connected server, ADR 0502) in the system browser rather than embedding a PDF viewer.
    private void OnOpenManual(object? sender, RoutedEventArgs e) =>
        SystemBrowser.Open($"{DesktopClientOptions.ApiBaseUrl}/download/manual/SimplArchive-Manual.pdf");

    // Help ▸ About (ADR 0504): the vendor block + the running client version.
    private void OnShowAbout(object? sender, RoutedEventArgs e) =>
        Safe.Fire(async () => await new AboutDialog().ShowDialog(this));

    // Refresh the notifications when the bell opens (ADR "Notification viewer + click-through"); the flyout opens
    // automatically via Button.Flyout.
    private void OnBellClick(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.LoadNotificationsAsync();
        }
    });

    // Impersonate the selected user (ADR "User impersonation"): swap the session to act as them.
    private void OnImpersonatePrincipal(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedPrincipal is { IsGroup: false } p)
        {
            await vm.ImpersonateAsync(p.Id);
        }
    });

    private void OnResetPrincipalMfa(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        var message = $"Disable two-factor authentication for '{p.Name}'? They'll be able to sign in with just their password until they re-enroll.";
        if (await new ConfirmDialog(message, "Reset").ShowDialog<bool>(this))
        {
            await vm.ResetSelectedUserMfaAsync();
        }
    });

    // ---- Legal holds (ADR "Legal hold & retention enforcement") ------------------------------------

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

    private void OnNewLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new LegalHoldDialog().ShowDialog<LegalHoldDialog.Result?>(this) is { } result)
        {
            await vm.CreateLegalHoldAsync(result.Name, result.Reason, null);
        }
    });

    private void OnReleaseLegalHold(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
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

    private void OnRemoveLegalHoldItem(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: LegalHoldItemRowViewModel row })
        {
            await vm.RemoveHoldItemAsync(row);
        }
    });

    // Open (context menu): same as the ribbon Open button / double-click — a folder (or a document with
    // children, e.g. an email with attachments) drills in, a plain document opens in its native application.
    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
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
    private void OnCheckoutCompare(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api ||
            (sender as Control)?.Tag is not CheckoutRowViewModel { Item: { } checkout } row)
        {
            return;
        }

        var ccvm = new CompareCheckoutViewModel();
        await ccvm.SetupAsync(api, checkout, row.DisplayName, row.FileExtension, row.StashDownloadUrl);
        await new CompareCheckoutDialog(ccvm).ShowDialog(this);
    });

    private void OnManageAccess(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.Api is not { } api || vm.SelectedItem is not { } node)
        {
            return;
        }

        var mvm = new ManageAccessViewModel();
        await mvm.SetupAsync(api, node.Id, node.Name);
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
            await vm.PromotePrimaryLocationAsync(references.ItemId, r.FolderId);
        }
        else
        {
            // Open the chosen folder AND select the item for viewing — its real row in the primary location, or its
            // reference (shortcut) row in a referencing folder.
            await vm.OpenFolderAsync(r.FolderId, references.ItemId);
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

    // ---- Inbox (ADR "S3-backed inbox", phase 2) -------------------------------------------------------

    private static InboxItemViewModel? InboxItemFrom(object? sender) =>
        (sender as Control)?.Tag as InboxItemViewModel;

    private void OnInboxItemDoubleTapped(object? sender, TappedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.OpenServerInboxItemCommand.ExecuteAsync(null);
        }
    });

    private void OnInboxOpen(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && InboxItemFrom(sender) is { } item)
        {
            vm.SelectedServerInboxItem = item;
            await vm.OpenServerInboxItemCommand.ExecuteAsync(null);
        }
    });

    // File a server-inbox item into a folder chosen from the picker.
    private void OnInboxFile(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || InboxItemFrom(sender) is not { } item ||
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
            await vm.FileServerInboxItemAsVersionAsync(item, result.TargetId, result.Comment);
        }
        else
        {
            await vm.FileServerInboxItemAsync(item, result.TargetId, result.Comment);
        }
    });

    // "File multiple items": bulk-file the selected server inbox items into one folder (ADR "Bulk-file multiple
    // inbox items").
    private void OnInboxFileMultiple(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var items = ServerInboxList.SelectedItems?.OfType<InboxItemViewModel>().ToList() ?? [];
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

    // Track the server-inbox selection count so the "File multiple items" button shows only for 2+.
    private void OnServerInboxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CanFileMultiple = (ServerInboxList.SelectedItems?.Count ?? 0) >= 2;
        }
    }

    // OS files dropped onto the inbox list upload straight into the S3-backed inbox (ADR "Inbox file-list
    // drop-zone"). Only external files are accepted (no internal row drag on the inbox). The classic DataObject/
    // DragEventArgs.Data API is used across the drag-drop code (see the note by the region below), suppressed.
#pragma warning disable CS0618
    private static void OnInboxDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnInboxDrop(object? sender, DragEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var storageFiles = e.Data.GetFiles()?.OfType<IStorageFile>().ToList();
        if (storageFiles is not { Count: > 0 })
        {
            return;
        }

        var files = new List<(string Name, byte[] Bytes)>();
        foreach (var file in storageFiles)
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            files.Add((file.Name, buffer.ToArray()));
        }

        await vm.UploadFilesToInboxAsync(files);
    });
#pragma warning restore CS0618

    private void OnInboxDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || InboxItemFrom(sender) is not { } item)
        {
            return;
        }

        if (await new ConfirmDialog($"Delete '{item.Name}' from the inbox?", "Delete").ShowDialog<bool>(this))
        {
            await vm.DeleteServerInboxItemAsync(item);
        }
    });

    // "Send to…" (ADR 0532): hand an own item to a chosen group or user via the picker dialog.
    private void OnInboxSend(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || InboxItemFrom(sender) is not { } item)
        {
            return;
        }

        var targets = await vm.GetInboxSendTargetsAsync();
        if (await new SendToInboxDialog(item.Name, targets).ShowDialog<SimplArchiveApiClient.InboxTargetInfo?>(this) is { } target)
        {
            await vm.SendInboxItemAsync(item, target);
        }
    });

    // "Move to my inbox" (ADR 0532): claim a group / other-user item into my own inbox.
    private void OnInboxMoveToMine(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && InboxItemFrom(sender) is { } item)
        {
            await vm.MoveInboxItemToMineAsync(item);
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

    // Ctrl/Cmd+P → the server manager (ADR "Desktop server configuration").
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            _ = new ServerManagerWindow().ShowDialog(this);
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

    // Begin an internal move/reference drag once the pointer leaves the pressed row by a small threshold.
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ContentsList).Properties.IsLeftButtonPressed
            && FindDataContext<NodeViewModel>(e.Source) is { } node)
        {
            _dragCandidate = node;
            _dragStart = e.GetPosition(ContentsList);
            // Snapshot the full multi-selection now — the ListBox is about to collapse it to the pressed row.
            _dragSelection = ContentsList.SelectedItems?.OfType<NodeViewModel>().ToList() ?? [];
        }
    }

    // Avalonia 11.3's replacement DataTransfer API is still stabilising; the classic DataObject /
    // DragEventArgs.Data / DragDrop.DoDragDrop members remain functional, so we use them across the whole
    // drag-and-drop region and suppress the obsolete warnings here.
#pragma warning disable CS0618
    private void OnListPointerMoved(object? sender, PointerEventArgs e) => Safe.Fire(async () =>
    {
        if (_dragCandidate is not { } node)
        {
            return;
        }

        if (!e.GetCurrentPoint(ContentsList).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }

        var delta = e.GetPosition(ContentsList) - _dragStart;
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
        if (source.Count > 1 && ContentsList.SelectedItems is { } sel)
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
        if (DataContext is MainWindowViewModel dragVm && dragVm.Api is { } dragApi)
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
        if (e.GetCurrentPoint(FolderTree).Properties.IsLeftButtonPressed
            && FindDataContext<TreeNodeViewModel>(e.Source) is { IsSynthetic: false, IsLauncher: false, IsPersonal: false } node)
        {
            _treeDragCandidate = node;
            _treeDragStart = e.GetPosition(FolderTree);
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e) => Safe.Fire(async () =>
    {
        if (_treeDragCandidate is not { } node)
        {
            return;
        }

        if (!e.GetCurrentPoint(FolderTree).Properties.IsLeftButtonPressed)
        {
            _treeDragCandidate = null;
            return;
        }

        var delta = e.GetPosition(FolderTree) - _treeDragStart;
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
        if (!node.IsReference && DataContext is MainWindowViewModel dragVm && dragVm.Api is { } dragApi)
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
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Internal move/reference drag (identified by the in-flight _internalDrag field, not a DataObject format):
        // the dragged items dropped on a folder row file into that folder; dropped anywhere else in the pane file
        // into the currently-open folder.
        if (_internalDrag is { } dragged)
        {
            var node = FindDataContext<NodeViewModel>(e.Source);
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

        var target = FindDataContext<NodeViewModel>(e.Source);

        // Dropped onto a document row → the inbox-style filing dialog: file as a new version of it, or into its
        // folder, with an optional comment (ADR "List-pane drop filing").
        if (target is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false }
            && vm.CreateDropFilingPickerViewModel(target, files.Count) is { } picker)
        {
            await picker.LoadAsync();
            var result = await new FolderPickerDialog { DataContext = picker }.ShowDialog<FilingResult?>(this);
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
        // The tree accepts an internal move/reference drag (the _internalDrag field) — never an OS-file drop.
        e.DragEffects = _internalDrag is not null || e.Data.Contains(NodeDragFormat)
            ? DragDropEffects.Copy | DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm
            && _internalDrag is { } dragged
            && FindDataContext<TreeNodeViewModel>(e.Source) is { } treeNode)
        {
            await PerformDropAsync(vm, dragged, treeNode.Id);
        }
    });
#pragma warning restore CS0618

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
            .ShowDialog<DropAction?>(this);

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

    // Walks up the visual tree from a routed-event source to the nearest DataContext of type T.
    private static T? FindDataContext<T>(object? source) where T : class
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
