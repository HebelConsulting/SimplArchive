using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The demo population and self-test routines the HEADLESS VERIFICATION HOOKS drive (CLAUDE.md, "Headless
// verification hooks"): --screenshot and its variants seed a representative workbench here, and the --*-test
// flags call the SelfTestAsync methods and compare what the view model then reports.
//
// Its own partial because 640 lines of verification support had accumulated inside the production view model,
// under a section header that said "Author identity card" -- a heading that had been true of the first thirty
// lines and of nothing since. That is the same shape DesktopClient/Program.cs had: the hooks are cheap to add
// and each one lands wherever the cursor happened to be.
//
// A PARTIAL rather than a collaborator, for the reason the tab partials give: every routine here writes the
// view model's own state -- the tree, the preview, the selection, the mask editor -- so a separate class would
// take the whole view model as a parameter and be a partial wearing a constructor.
//
// They are `internal` rather than private because the desktop test project sees internals, and several are
// called from ScreenshotRenderer and Program.cs. That is also the reason this code cannot simply move to the
// test project: it seeds a running application's view model, not a fixture.
public partial class MainWindowViewModel
{
    // Populates a representative logged-in workbench for the headless UI screenshot (no network).
    internal void PopulateDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        CanCreateFolder = true;
        IsTenantAdmin = true;
        // An unread notification, so the bell badge is IN the demo renders. It clipped on the right for months
        // and no screenshot could have shown it, because no captured state ever had an unread count.
        UnreadNotificationCount = 2;
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Demo Repository", FolderId = Guid.NewGuid(), ShowSeparator = true });
        // Mirror the real tree's top-level nodes: a Personal repository (ADR 0370) and, for a tenant admin, the
        // synthetic Administration branch (ADR 0377), around the shared repositories.
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Demo Admin", true, null, isPersonal: true)); // named after its owner (ADR 0671)
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Demo Repository", true, null));
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Invoices", false, null, hasChildren: false)); // an EMPTY folder — shows the pastel glyph (ADR "Empty-folder tree icon")
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Shared (ref)", false, null, isReference: true));
        Tree.Add(new TreeNodeViewModel(Guid.Empty, "Administration", true, null, syntheticIcon: "mdi-shield-account"));
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "Invoices", HasChildren = true, HasVersions = false });
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "Invoice 2025-001.pdf", HasChildren = false, HasVersions = true });
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "sample.docx", HasChildren = false, HasVersions = true });
        Items.Add(new NodeViewModel { Id = Guid.Empty, Name = "Shared Contract.pdf", HasChildren = false, HasVersions = true, IsReference = true });
        SelectedItem = Items[1]; // a document is picked, so Rename/Delete/Download are enabled in the screenshot
        DetailTitle = "Invoice 2025-001";
        SysName = "Invoice 2025-001";
        SysFileExtension = ".pdf";
        SysCreated = "2026-07-15 09:12";
        SysCreatedBy = "Demo Admin";
        SysDocumentDate = new DateTime(2026, 6, 28);
        SysHasTiff = false;
        SysOcrLanguages = "German, French";
        MaskLine = "Mask: Basic Entry · version 1";
        CanEditDetail = true;
        IndexFields.Add(new IndexFieldViewModel { FieldName = "Keywords", Values = "invoice, reviewed" });
        Preview.PreviewConverted = false;
        Preview.Reset("Preview renders here (PDF/image/text).");
        // The thread mixes what the product records AUTOMATICALLY (ADR 0545) with what a person typed — which is
        // what a real feed looks like. The fixture previously held only the typed comment, so the manual showed a
        // chat pane that the product no longer produces.
        //
        // Note this fixture is synthetic: it does not come from the demo seed, so it does not follow the product
        // on its own. It has to be updated by hand whenever the thread gains something new — see the backlog entry
        // on the desktop capture being fixture-driven.
        // ONE entry for the filing, not two: a first version IS the document arriving, and it carries the version
        // chip and check-in comment (ADR 0545). The fixture used to hold the separate "filed a new document"
        // entry beside this one, which is exactly the duplication the product stopped producing.
        Comments.Add(new ChatMessageViewModel
        {
            Id = Guid.Empty,
            AuthorName = "Demo Admin",
            Body = string.Empty,
            Kind = 1,
            VersionNumber = 1,
            VersionComment = "Scanned from the paper original.",
            CreatedAt = ScreenshotClock,
        });
        // "Demo Admin", not the email address: the author label became DisplayName when identity cards landed
        // (ADR 0544), and this fixture still showed the raw email the product used to render.
        //
        // CanReply + a reply of its own, so the capture shows the thread the product can actually produce
        // (issue #383) rather than a flat list — the affordance is on the message in the manual because it is on
        // the message in the app.
        var typed = new ChatMessageViewModel
        {
            Id = Guid.Empty,
            AuthorName = "Demo Admin",
            Body = "Looks good.",
            CreatedAt = ScreenshotClock,
            CanReply = true,
        };
        typed.Replies.Add(new ChatMessageViewModel
        {
            Id = Guid.Empty,
            AuthorName = "Demo Admin",
            Body = "Filed under Invoices.",
            CreatedAt = ScreenshotClock,
        });
        Comments.Add(typed);
        Status = "3 item(s).";
    }

    // Populates the Tasks tab for the headless screenshot (ADR "Workflow / document state model", 0009). The
    // workflow itself is now a separate on-demand window (ADR "Workflow start on demand"), so it isn't part of
    // this main-window screenshot.
    internal void PopulateWorkflowDemoForScreenshot()
    {
        PopulateDemoForScreenshot();

        Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Q3 Invoice.pdf", VersionNumber = 2, AssignedAt = ScreenshotClock.AddHours(-3), DueAt = ScreenshotClock.AddDays(-1) });
        Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Vendor Contract.docx", VersionNumber = 1, AssignedAt = ScreenshotClock.AddDays(-1), DueAt = ScreenshotClock.AddDays(3) });
        TaskCount = Tasks.Count;
        RebuildVisibleTasks();
    }

    // Populates the pane edit mode (a document selected, Edit pressed) for the headless screenshot — the whole
    // pane is editable: system fields (Name/Document date/OCR languages) plus the mask + index fields.
    internal void PopulateMaskEditForScreenshot()
    {
        PopulateDemoForScreenshot();
        AvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
        AvailableMasks.Add(new MaskChoiceViewModel(Guid.NewGuid(), "Basic Entry"));
        AvailableMasks.Add(new MaskChoiceViewModel(Guid.NewGuid(), "eMail"));
        SelectedMaskChoice = AvailableMasks[1];
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Keywords", "MultiSelect", false), ["finance", "quarterly"]));
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Amount", "Number", true), ["1240"]));
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Due date", "Date", false), ["2026-07-28"]));
        MaskEditFields.Add(MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Paid", "Boolean", false), ["true"]));
        IsEditing = true;
    }

    // Headless exercise of breadcrumb building/navigation against a running Api (see Program --breadcrumb-test).
    // The self-tests' scratch parent, and the reason it is not simply Tree[0].
    //
    // Tree[0] is the PERSONAL space, whose first level holds only the folders it was provisioned with (#634) —
    // so a scratch folder created there is refused, exactly as a user's would be. It goes inside My Documents
    // instead, which is where a user's own folders go, so these self-tests exercise the path a user takes.
    //
    // The name is a literal because this project does not reference the Domain (it is a client, and talks to
    // the API over HTTP); it is a server-side folder name rather than a localized label, so it does not move.
    private async Task<TreeNodeViewModel> ScratchParentAsync()
    {
        var personal = Tree[0];
        await personal.ReloadChildrenAsync();
        return personal.Children.First(c => c.Name == "My Documents");
    }

    internal async Task<List<string>> BreadcrumbSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var trail = new List<string>();

        await LoadRootAsync();
        trail.Add(BreadcrumbTrail());

        var repositories = await _api!.Documents.GetRepositoriesAsync();
        var repositoryNode = new TreeNodeViewModel(repositories[0].Id, repositories[0].Name, repositories[0].HasSubfolders, LoadTreeChildrenAsync, links: repositories[0].Links);
        SetBreadcrumbFromTreeNode(repositoryNode);
        await LoadFolderContentsAsync(repositoryNode.Id, repositoryNode.Links);
        trail.Add(BreadcrumbTrail());

        if (Items.FirstOrDefault(i => i.IsFolder) is { } folder)
        {
            Breadcrumbs.Add(new BreadcrumbViewModel { Name = folder.Name, FolderId = folder.Id, ShowSeparator = true });
            await LoadFolderContentsAsync(folder.Id, folder.Links);
            trail.Add(BreadcrumbTrail());

            // Click the repository crumb (index 1) to navigate back up.
            await NavigateToBreadcrumbCommand.ExecuteAsync(Breadcrumbs[1]);
            trail.Add(BreadcrumbTrail());
        }

        return trail;
    }

    private string BreadcrumbTrail() => string.Join(" / ", Breadcrumbs.Select(b => b.Name)) + $"  [{Items.Count} items]";

    // Populates the Search tab for the headless screenshot (no network).
    internal void PopulateSearchDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        SelectedTab = 3;
        Search.PopulateDemoForScreenshot();
    }


    // Headless exercise of referenced folders appearing in the tree (see Program --reftree-test): references
    // a folder into another folder, then confirms the tree's child loader returns a shortcut node for it
    // whose Id is the target (so it expands the target's subtree).
    internal async Task<List<string>> RefTreeSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var log = new List<string>();

        var root = (await _api!.Documents.GetRepositoriesAsync())[0];
        var s = Guid.NewGuid().ToString("N")[..6];
        await _api.Documents.CreateFolderAsync(root.Href("children"), $"rtree-A-{s}");
        await _api.Documents.CreateFolderAsync(root.Href("children"), $"rtree-B-{s}");
        var a = (await _api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rtree-A-{s}");
        var b = (await _api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rtree-B-{s}");
        await _api.Documents.CreateFolderAsync(a.Href("children"), $"rtree-F-{s}");
        var f = (await _api.Documents.GetChildrenAsync(a.Href("children"))).First(c => c.Name == $"rtree-F-{s}");

        await _api.References.CreateReferenceAsync(b.Href("references"), f.Id);

        var bTreeChildren = (await LoadTreeChildrenAsync(new TreeNodeViewModel(b.Id, b.Name, false, null, links: b.Links))).ToList();
        var refNode = bTreeChildren.FirstOrDefault(n => n.IsReference);
        log.Add(refNode is not null && refNode.Id == f.Id && refNode.IconValue == "mdi-folder-arrow-right"
            ? "OK: referenced folder appears in the tree as a shortcut node targeting F."
            : "FAILED: referenced folder missing from the tree.");

        await _api.Documents.DeleteAsync(a.Href("self"));
        await _api.Documents.DeleteAsync(b.Href("self"));
        return log;
    }

    // Headless exercise of the tree refresh (see Program --treerefresh-test): creates a sub-folder inside the
    // first repository, then confirms the rebuilt tree's lazy-loader returns fresh children including it, and
    // that Refresh repopulates the tree.
    internal async Task<List<string>> TreeRefreshSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var log = new List<string>();

        await LoadRootAsync();
        log.Add($"tree roots: {Tree.Count}");

        var repository = (await _api!.Documents.GetRepositoriesAsync())[0];
        var name = $"treetest-{Guid.NewGuid():N}"[..16];
        await LoadFolderContentsAsync(repository.Id, repository.Links);
        await CreateFolderAsync(name);

        var treeChildren = (await LoadTreeChildrenAsync(new TreeNodeViewModel(repository.Id, repository.Name, false, null, links: repository.Links))).Select(n => n.Name).ToList();
        log.Add(treeChildren.Contains(name) ? "OK: rebuilt tree loader returns the new folder." : "FAILED: new folder missing from tree.");

        Tree.Clear();
        await RefreshCommand.ExecuteAsync(null);
        log.Add(Tree.Count > 0 ? "OK: Refresh repopulated the tree." : "FAILED: Refresh left the tree empty.");

        // Clean up the test folder.
        var created = (await _api.Documents.GetChildrenAsync(repository.Href("children"))).First(c => c.Name == name);
        await _api.Documents.DeleteAsync(created.Href("self"));
        return log;
    }

    // Headless regression for the tree-select desync bugfix (see DesktopTreeSelectTests): after drilling into a
    // subfolder via the contents list, the tree's selected node is unchanged, so re-tapping it must STILL
    // reload the list — the [ObservableProperty] SelectedTreeNode setter short-circuits a same-reference
    // re-selection (so OnSelectedTreeNodeChanged never fires), and ReselectTreeFolderAsync (the Tapped
    // handler's target) is what closes that gap. Ordering is controlled here so the async-void selection
    // handler can't race the deterministic loads. Returns the folder shown after the list-drill and after the
    // re-tap, plus the repo's re-listed item names.
    internal async Task<(Guid Parent, Guid AfterDrill, Guid AfterRetap, string[] Items)> TreeReselectSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();

        // The user selects the repo in the tree (loads its contents). Set the selection directly rather than
        // via the property, so the async-void OnSelectedTreeNodeChanged handler's load can't race the loads
        // below — this leaves the exact state a real first-select produces (repo is the selected node).
#pragma warning disable MVVMTK0034 // deliberately set the backing field to avoid firing the change handler
        _selectedTreeNode = repo;
#pragma warning restore MVVMTK0034
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var sub = Items.FirstOrDefault(n => !n.HasVersions);
        if (sub is null)
        {
            // Other tests sharing the demo tenant may have removed the seeded subfolder; create one so this
            // self-test doesn't depend on test ordering.
            await _api!.Documents.CreateFolderAsync(repo.Href("children"), "tree-select-" + Guid.NewGuid().ToString("N")[..8]);
            await LoadFolderContentsAsync(repo.Id, repo.Links);
            sub = Items.First(n => !n.HasVersions);
        }

        // …then drills into the subfolder via the CONTENTS list — the tree's selection stays on the repo.
        await LoadFolderContentsAsync(sub.Id, sub.Links);
        var afterDrill = _currentFolderId!.Value;

        // Re-tap the still-selected repo node in the tree: the fix reloads the list back to the repo.
        await ReselectTreeFolderAsync(repo);
        // The parent is RETURNED rather than left for the caller to guess: it is the personal space's
        // My Documents (#634), not Tree[0], and a test asserting against the root would be asserting
        // about a folder this self-test never touched.
        return (repo.Id, afterDrill, _currentFolderId!.Value, Items.Select(n => n.Name).ToArray());
    }

    // Search-hit reveal-in-tree (issue #340): activating a document search hit expands + selects its parent folder
    // in the tree, loads that folder into the list, and selects the document there. Seeds a nested doc so the reveal
    // has a real ancestor chain (repo → subfolder → doc), collapses the tree + moves the list away, then drives the
    // real OpenSearchResultAsync and reports whether the tree, list, and list-selection all landed on the target.
    internal async Task<(bool TreeSelectedParent, bool ListHasDoc, bool ListSelectedDoc)> SearchRevealSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();

        // Seed a subfolder + a document inside it (independent of test ordering).
        var subName = "reveal-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), subName);
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var sub = Items.First(n => n.IsFolder && !n.IsReference && n.Name == subName);
        var docName = "reveal-doc-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var docId = await _api.Documents.UploadFileAsync(sub.Href("children"), docName, System.Text.Encoding.UTF8.GetBytes("reveal me"));

        // Start from a clean slate: nothing selected in the tree, the list showing the repo root (not the subfolder).
        await LoadFolderContentsAsync(repo.Id, repo.Links);
#pragma warning disable MVVMTK0034 // set the backing field so the reveal's selection is a real change, not a no-op
        _selectedTreeNode = null;
#pragma warning restore MVVMTK0034

        // Activate the search hit for the seeded document — the real path a double-click drives. A real hit
        // carries its advertised addresses (#443), so the synthetic one does too, built from the rows in hand.
        var docRow = (await _api.Documents.GetChildrenAsync(sub.Href("children"))).Single(n => n.Id == docId);
        await OpenSearchResultAsync(new SearchResultViewModel
        {
            Id = docId,
            Name = docName,
            IsFolder = false,
            ParentId = sub.Id,
            Path = string.Empty,
            Links = new Dictionary<string, string> { ["self"] = docRow.Href("self"), ["parent"] = sub.Href("self") },
        });

        return (
            SelectedTreeNode?.Id == sub.Id,                               // parent folder revealed + selected in the tree
            Items.Any(n => n.Id == docId && !n.IsReference),             // the document is listed in the list pane
            SelectedItem?.Id == docId);                                  // …and selected there
    }

    // The references dialog's "Open" must open the chosen folder AND select the item for viewing — its real row in
    // the primary location, and its reference (shortcut) row in a referencing folder (that reference row was
    // previously skipped because the selection filtered out references). Drives OpenFolderAsync exactly as the
    // dialog's Open path does.
    internal async Task<(bool SelectedInPrimary, bool SelectedReferenceInRefFolder)> OpenReferenceSelectsDocumentSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();

        // A document filed at the repo root (its primary location) and a subfolder that references it.
        var refFolderName = "refopen-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), refFolderName);
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var refFolder = Items.First(n => n.IsFolder && !n.IsReference && n.Name == refFolderName);
        var docName = "refopen-doc-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var docId = await _api.Documents.UploadFileAsync(repo.Href("children"), docName, System.Text.Encoding.UTF8.GetBytes("body"));
        await _api.References.CreateReferenceAsync(refFolder.Href("references"), docId);

        // Open the primary location selecting the doc → its real (non-reference) row is selected.
        await OpenFolderAsync(repo.DocumentSelfHref, docId);
        var primaryOk = SelectedItem is { IsReference: false } primary && primary.Id == docId;

        // Open the referencing folder selecting the doc → its reference (shortcut) row is selected for viewing.
        await OpenFolderAsync(refFolder.DocumentSelfHref, docId);
        var refOk = SelectedItem is { IsReference: true } shortcut && shortcut.Id == docId;

        return (primaryOk, refOk);
    }

    // Repository sort order (issue #339): folders always come first, alphabetically, then documents; the tree's
    // folder children are alphabetical too. Seeds subfolders in NON-alphabetical creation order + a document, then
    // checks the list order and the tree-child order both landed alphabetically-folders-first.
    internal async Task<(bool ListFoldersAlphaThenDoc, bool TreeFoldersAlpha)> RepositorySortSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();

        var parentName = "sort-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), parentName);
        await LoadFolderContentsAsync(repo.Id, repo.Links);
        var parent = Items.First(n => n.IsFolder && !n.IsReference && n.Name == parentName);

        // Subfolders created out of alphabetical order + a document filed alongside them.
        await _api.Documents.CreateFolderAsync(parent.Href("children"), "Zebra");
        await _api.Documents.CreateFolderAsync(parent.Href("children"), "Apple");
        await _api.Documents.CreateFolderAsync(parent.Href("children"), "Mango");
        await _api.Documents.UploadFileAsync(parent.Href("children"), "a-document.txt", System.Text.Encoding.UTF8.GetBytes("doc"));

        // List: folders first (alphabetical), then the document — regardless of creation order.
        await LoadFolderContentsAsync(parent.Id, parent.Links);
        var listOk = Items.Select(n => n.Name).SequenceEqual(["Apple", "Mango", "Zebra", "a-document"]);

        // Tree: expand the parent; its folder children are alphabetical (the document isn't a tree node).
        await repo.EnsureExpandedAsync();
        var parentNode = repo.Children.First(n => n.Id == parent.Id);
        await parentNode.EnsureExpandedAsync();
        var treeOk = parentNode.Children.Where(n => !n.IsLauncher && !n.IsSynthetic)
            .Select(n => n.Name).SequenceEqual(["Apple", "Mango", "Zebra"]);

        return (listOk, treeOk);
    }

    // Navigating to a different folder clears the panes right of the list (index-data / preview / comments) so a
    // freshly-selected folder doesn't keep showing the previously-viewed document — parity with the web client
    // (ADR 0516). A same-folder reload keeps the current detail. Driven against the running Api; DetailTitle stands
    // in for the populated detail panes (what a real selection sets and what ClearDetail resets).
    internal async Task<(bool ClearedOnFolderChange, bool KeptOnSameFolderReload)> FolderChangeResetsPanesSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();
        await LoadFolderContentsAsync(repo.Id, repo.Links);

        // A guaranteed different folder to navigate into — create one if the shared demo tenant has none.
        var sub = Items.FirstOrDefault(n => n.IsFolder && !n.IsReference);
        if (sub is null)
        {
            await _api!.Documents.CreateFolderAsync(repo.Href("children"), "panereset-" + Guid.NewGuid().ToString("N")[..8]);
            await LoadFolderContentsAsync(repo.Id, repo.Links);
            sub = Items.First(n => n.IsFolder && !n.IsReference);
        }

        // Populate the detail (as selecting a document does), then navigate to a DIFFERENT folder: the previous
        // subject must not survive the move. The sentinel being GONE, not the pane being empty — emptiness is
        // what ADR 0703 replaced. See DesktopFolderPaneResetTests for why the positive half is asserted there.
        DetailTitle = "sentinel-A";
        await LoadFolderContentsAsync(sub.Id, sub.Links);
        var clearedOnFolderChange = DetailTitle != "sentinel-A";

        // A same-folder reload (e.g. after an in-place operation) must KEEP the current detail.
        DetailTitle = "sentinel-B";
        await LoadFolderContentsAsync(sub.Id, sub.Links);
        var keptOnSameFolderReload = DetailTitle == "sentinel-B";

        return (clearedOnFolderChange, keptOnSameFolderReload);
    }

    // Exercises the tree-pane context menu's Manage-access action (ADR "Tree-pane context menu with
    // manage-access") on the node kinds only the TREE exposes: a repository ROOT (never a contents-list row, so
    // the list-row menu could never reach it) and a nested subfolder. Returns the ACL round-trip result for each,
    // proving a tree node's Id is a valid ACL target the way OnTreeManageAccess uses it.
    internal async Task<(bool RootGranted, bool SubfolderGranted)> TreeManageAccessSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var root = Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "treeacl-" + suffix;
        await CreateSubfolderAsync(root.Id, root.Href("children"), name);
        await root.ReloadChildrenAsync();
        var sub = root.Children.First(c => c.Name == name);

        var granteeId = await _api!.Admin.CreateUserAsync($"treeacl-{suffix}@simplarchive.local", $"TreeAcl {suffix}");
        var viewer = new AclRights(
            CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false,
            CanCreateSubItems: false, CanDelete: false, CanMove: false, CanAnnotate: false, CanManagePermissions: false);

        var rootGranted = await GrantAndRevokeAsync(root.DocumentSelfHref, granteeId.Id, viewer);
        var subGranted = await GrantAndRevokeAsync(sub.DocumentSelfHref, granteeId.Id, viewer);

        await DeleteFolderAsync(sub.Id, sub.Href("self")); // clean up
        return (rootGranted, subGranted);
    }

    // Exercises the tree context menu's folder actions that act on the RIGHT-CLICKED node rather than the
    // contents-list selection (ADR "Tree-pane context menu"): move a tree folder under another folder, and place
    // a reference (shortcut) to it elsewhere. Returns whether each landed where it should.
    internal async Task<(bool Moved, bool Referenced)> TreeFolderMoveAndReferenceSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var root = Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var subjectName = $"treemove-{suffix}";
        var destinationName = $"treedest-{suffix}";
        await CreateSubfolderAsync(root.Id, root.Href("children"), subjectName);
        await CreateSubfolderAsync(root.Id, root.Href("children"), destinationName);
        var children = await _api!.Documents.GetChildrenAsync(root.Href("children"));
        var subject = children.First(c => c.Name == subjectName);
        var destination = children.First(c => c.Name == destinationName);

        await MoveFolderAsync(subject.Href("self"), subject.Name, destination.Id);
        var moved = (await _api.Documents.GetChildrenAsync(destination.Href("children"))).Any(c => c.Id == subject.Id);

        // Place a reference to the moved folder back under the repository root.
        await PlaceReferenceAsync(subject.Id, subject.Name, root.Href("references"));
        var referenced = (await _api.References.GetReferencesAsync(root.Href("references"))).Any(r => r.TargetId == subject.Id);

        await DeleteFolderAsync(destination.Id, destination.Href("self")); // clean up (takes the subject with it)
        return (moved, referenced);
    }

    // Exercises the empty-folder tree icon (ADR "Empty-folder tree icon", issue #352) against the real Api: a
    // freshly-created folder is empty; the same folder holding only a DOCUMENT is not (the distinction the flag
    // must not get wrong, since a documents-only folder is still a leaf in the folders-only tree).
    internal async Task<(bool EmptyWhenNew, bool NotEmptyWithADocument)> EmptyFolderIconSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var root = Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });

        var name = "treeempty-" + Guid.NewGuid().ToString("N")[..8];
        await CreateSubfolderAsync(root.Id, root.Href("children"), name);
        await root.ReloadChildrenAsync();
        var emptyWhenNew = root.Children.First(c => c.Name == name).IsEmptyFolder;

        var child = root.Children.First(c => c.Name == name);
        await _api!.Documents.UploadFileAsync(child.Href("children"), "a-document.txt", System.Text.Encoding.UTF8.GetBytes("doc"));
        await root.ReloadChildrenAsync();
        var notEmptyWithADocument = !root.Children.First(c => c.Name == name).IsEmptyFolder;

        await DeleteFolderAsync(child.Id, child.Href("self")); // clean up
        return (emptyWhenNew, notEmptyWithADocument);
    }

    private async Task<bool> GrantAndRevokeAsync(string documentSelfHref, Guid granteeId, AclRights rights)
    {
        // Grant through the principal row the ACL view offers, then revoke through the entry row that grant
        // produced — the same path the dialog takes, which is the point of a self-test (ADR 0555).
        var before = await _api!.Documents.GetAclAsync(documentSelfHref);
        await _api.Documents.SetAclEntryAsync(before.Principals.Single(p => p.Type == "users" && p.Id == granteeId), rights);

        var after = await _api.Documents.GetAclAsync(documentSelfHref);
        var entry = after.Entries.FirstOrDefault(e => e.PrincipalType == "users" && e.PrincipalId == granteeId);
        var granted = entry is { Rights.CanSee: true };
        if (entry is not null)
        {
            await _api.Documents.RevokeAclEntryAsync(entry);
        }

        return granted;
    }

    // Exercises the tree-pane folder context-menu actions (ADR "Desktop tree-pane folder context menu") end to
    // end against the running Api: create a subfolder under a repository, rename it, delete it.
    internal async Task<(bool Created, bool Renamed, bool Deleted)> TreeFolderActionsSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();

        var name = "treeact-" + Guid.NewGuid().ToString("N")[..8];
        await CreateSubfolderAsync(repo.Id, repo.Href("children"), name);
        var created = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).FirstOrDefault(c => c.Name == name);
        if (created is null)
        {
            return (false, false, false);
        }

        var renamed = name + "-r";
        await RenameFolderAsync(created.Href("self"), renamed);
        var isRenamed = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).Any(c => c.Id == created.Id && c.Name == renamed);

        await DeleteFolderAsync(created.Id, created.Href("self"));
        var isDeleted = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).All(c => c.Id != created.Id);

        return (true, isRenamed, isDeleted);
    }

    // Creating a subfolder must NOT collapse the tree — the parent folder (whose contents are shown) stays in the
    // tree, expanded, now showing the new child (ADR "Keep the desktop tree expanded on a structural change", see
    // DesktopTreeFolderActionsTests). Reference-equality on the parent node distinguishes the targeted reload from
    // the old full rebuild (which replaced every node).
    internal async Task<bool> NewFolderKeepsTreeExpandedSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();
        await repo.ReloadChildrenAsync(); // materialise + expand the folder node, as navigating into it would

        var name = "treeexp-" + Guid.NewGuid().ToString("N")[..8];
        await CreateSubfolderAsync(repo.Id, repo.Href("children"), name);

        // Reference-equality on the node is the whole point — it distinguishes the targeted reload from the old
        // full rebuild, which replaced every node. The scratch parent is a CHILD of the personal space now
        // (#634), so "still in the tree" is asked of its parent's children rather than of the roots.
        var stillExpanded = Tree[0].Children.Contains(repo) && repo.IsExpanded && repo.Children.Any(c => c.Name == name);

        var created = (await _api!.Documents.GetChildrenAsync(repo.Href("children"))).FirstOrDefault(c => c.Name == name);
        if (created is not null)
        {
            await DeleteFolderAsync(created.Id, created.Href("self"));
        }
        return stillExpanded;
    }

    // Per-folder contents sort order (ADR "Per-folder contents sort order", see DesktopFolderContentsSortTests):
    // a fresh folder defaults to DocumentDate; the detail-pane Save round-trips the choice to the server and
    // updates the VM state.
    internal async Task<bool> FolderContentsSortSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        await LoadRootAsync();
        var repo = await ScratchParentAsync();

        var name = "fsort-" + Guid.NewGuid().ToString("N")[..8];
        await _api!.Documents.CreateFolderAsync(repo.Href("children"), name);
        var folder = (await _api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == name);

        await OpenFolderAsync(folder.Href("self"));
        var defaultIsDocDate = _folderSortOrder == 1 && FolderSortText == Strings.Get("FolderSortDocDate");

        // Through the ONE detail edit now (issue #408): the order is a field in the pane, committed by the same
        // Save as the mask, rather than by a toggle of its own.
        await LoadDetailAsync(new NodeViewModel { Id = folder.Id, Name = name, HasChildren = false, HasVersions = false, Links = folder.Links });
        await BeginEditCommand.ExecuteAsync(null);
        EditSortOrder = 2; // Created
        await SaveDetailCommand.ExecuteAsync(null);
        var persisted = await _api.Documents.GetContentsSortOrderAsync(folder.Href("children")) == 2;
        var reflected = _detailSortOrder == 2 && !IsEditing && DetailSortText == Strings.Get("FolderSortCreated");

        await DeleteFolderAsync(folder.Id, folder.Href("self"));
        return defaultIsDocDate && persisted && reflected;
    }



    // Headless exercise of the Personal-space grouping (ADR "GUI-tree Personal space grouping", see
    // DesktopPersonalSpaceTreeTests): the Personal node nests the Intray + Check-out launcher nodes above its real
    // subfolders, and selecting a launcher switches to the matching bottom tab.
    internal async Task<List<string>> PersonalLaunchersSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        var log = new List<string>();

        await LoadRootAsync();
        var personal = Tree.FirstOrDefault(n => n.IsPersonal);
        if (personal is null)
        {
            log.Add("FAILED: no Personal node.");
            return log;
        }

        var children = (await LoadPersonalChildrenAsync(new TreeNodeViewModel(personal.Id, personal.Name, false, null, links: personal.Links))).ToList();
        log.Add(children is [{ PersonalKind: "intray", IsLauncher: true, LauncherTab: 1, IconValue: "mdi-inbox-arrow-down" },
        { PersonalKind: "checkout", IsLauncher: true, LauncherTab: 2, IconValue: "mdi-lock-open-variant-outline" }, ..]
            ? "OK: Intray + Check-out launchers nested first under Personal."
            : "FAILED: launcher nodes missing or out of order.");

        // Selecting the Intray launcher switches to the Intray bottom tab (index 1); the tab index is set
        // synchronously in the launcher branch before any await.
        SelectedTreeNode = children[0];
        log.Add(SelectedTab == 1 ? "OK: selecting the Intray launcher switched to tab 1." : $"FAILED: tab is {SelectedTab}.");

        SelectedTreeNode = children[1];
        log.Add(SelectedTab == 2 ? "OK: selecting the Check-out launcher switched to tab 2." : $"FAILED: tab is {SelectedTab}.");
        return log;
    }

    internal void SetPreviewPagesForScreenshot(IEnumerable<Bitmap> pages) => Preview.SetPreviewPagesForScreenshot(pages);

    internal void SetPreviewNotesForScreenshot(IReadOnlyList<NoteBox> notes) => Preview.SetScreenshotNotesOnFirstPage(notes);

    // Seeds a preview page with a bitmap + hit-overlay words and a find query, for the headless overlay
    // screenshot (no network).
    internal void PopulateHitOverlayForScreenshot(Bitmap image, IReadOnlyList<VersionsClient.TextLayoutBox> words, string query)
    {
        var page = new PreviewPageViewModel(image);
        page.SetWords(words);
        // Two sample sticky-note boxes (ADR "Post-it note boxes") — the second Selected — so the screenshot shows
        // the always-visible sized note rendering plus the multi-select outline (ADR "Annotation multi-select").
        page.Notes =
        [
            new NoteBox(Guid.NewGuid(), 0, 0.52, 0.12, 0.30, 0.10, "#FFEB3B", CanEdit: true, "A resizable sticky note that always shows its text."),
            new NoteBox(Guid.NewGuid(), 0, 0.52, 0.30, 0.30, 0.10, "#B3E5FC", CanEdit: true, "A second, selected note.", Selected: true),
        ];
        Preview.SetHitOverlayPageForScreenshot(page, query);
    }

    // Headless-screenshot mock (no Api) — a couple of groups + users with a rights matrix, for --users.
    public void PopulateUsersGroupsDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        UserDisplayName = "Demo Admin";
        CanManageUsers = true;
        Principals.Clear();
        Principals.Add(new PrincipalRowViewModel(true, Guid.NewGuid(), "Administrators", true,
            new AdminClient.SystemRightsData(true, false, false, false, false, false, true, true, true, true, true, true, true)));
        Principals.Add(new PrincipalRowViewModel(true, Guid.NewGuid(), "Editors", true,
            new AdminClient.SystemRightsData(false, false, false, false, false, false, true, false, false, false, false, false, false)));
        Principals.Add(new PrincipalRowViewModel(false, Guid.NewGuid(), "Demo Admin", true,
            new AdminClient.SystemRightsData(true, false, false, false, false, false, true, true, true, true, true, true, true)));
        Principals.Add(new PrincipalRowViewModel(false, Guid.NewGuid(), "Jane Doe", false,
            new AdminClient.SystemRightsData(false, false, false, false, false, false, false, false, false, false, false, false, false)));
        // Select the Administrators group so the rights matrix + Members section show (mock members, no API).
        SelectedPrincipal = Principals[0];
        GroupMembers.Add(new UserOptionInfo(Guid.NewGuid(), "Demo Admin"));
        GroupMembers.Add(new UserOptionInfo(Guid.NewGuid(), "Jane Doe"));
        HasGroupMembers = true;
        MemberCandidates.Add(new UserOptionInfo(Guid.NewGuid(), "Bob Smith"));
    }

    // A fixed reference "now" for the headless --screenshot demo stubs, so timestamps in the audit / tasks /
    // recycle-bin screens don't shift with the wall clock between runs — that made the auto-generated manual's
    // PDF differ on every regeneration (ADR 0510). The web capture freezes its demo clock the same way.
    internal static readonly DateTimeOffset ScreenshotClock = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    // Mocks the Audit tab for the headless screenshot (ADR "Desktop audit viewer").
    internal void PopulateAuditDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserDisplayName = "Demo Admin";
        IsTenantAdmin = true;
        CanViewAuditLog = true;
        Audit.PopulateDemoForScreenshot();
    }

    // Mocks the Tenant tab for the headless screenshot (ADR "Tenant-admin settings tab").
    internal void PopulateTenantSettingsDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserDisplayName = "Demo Admin";
        IsTenantAdmin = true;
        TenantName = "Demo Tenant";
        TenantAuditRetentionDays = 365;
        TenantCheckoutTtlDays = 14;
        TenantWormLockModeIndex = 0;
        TenantRequireMfa = true;
        TenantStorageQuotaMb = 250;
        TenantStorageUsage = "Used: 12.4 MB of 250 MB";
        TenantIncompleteUploadCleanupDays = 7;
        _tenantStagedOcrCodes = ["eng", "deu", "fra", "ita"];
        TenantOcrDisplay = "English, German, French, Italian";
        // Fixed, not minted (#832): the figure regenerates on every manual build, and a fresh GUID here made
        // desktop-tenant.png differ per run with nothing changed. This is the demo tenant's real (derived) id.
        TenantId = "746a22de-2d1c-5b70-8888-ea12c0c8ffec";
        TenantStatus = "Active";
        TenantCreated = ScreenshotClock.AddMonths(-8).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        TenantSettingsLoaded = true;
    }
}
