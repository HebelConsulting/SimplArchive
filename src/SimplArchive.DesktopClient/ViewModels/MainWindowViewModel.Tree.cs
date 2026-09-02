using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Building the tree pane and keeping it current: the roots, a full reload that preserves what the user had
// expanded, revealing a newly-created child, and the lazy per-node child loaders -- ordinary folders, the
// personal space, and the admin branch.
//
// It came out of a heading reading "Login", which was true of its first hundred lines -- sign in, sign out,
// bootstrap a session -- and of nothing for the hundred and sixty after them (#941).
//
// Removing _treeMemory from that section REPAIRED a comment rather than breaking one. "Bootstraps an
// already-authenticated session ..." had been left stranded above the field, describing
// InitializeSessionAsync two lines below it: the field had been inserted between a comment and its method.
// Taking the field away puts them back together. Sixth orphan comment found in this burn-down, and the second
// of the kind that is ACCURATE but attached to the wrong member -- which reads as correct documentation and is
// therefore worse than one that is visibly stale.
//
// A partial rather than a type of its own: every loader writes into this view model's own tree collection and
// reports through its status line.
public sealed partial class MainWindowViewModel
{
    private readonly TreeExpansionMemory _treeMemory = new();

    private async Task LoadRootAsync()
    {
        if (_api is null)
        {
            return;
        }

        await ReloadTreeAsync();

        Items.Clear();
        ClearDetail();
        _currentFolderId = null;
        _currentRepositoryId = null;
        CanCreateFolder = false;
        CanExport = false;
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbViewModel { Name = "Repositories", FolderId = null, ShowSeparator = false });
        Status = string.Format(Strings.Get("StRepositories"), Tree.Count);
    }

    // Rebuilds the folders-only tree from the top (repository roots), collapsed. The tree lazy-loads and
    // caches each node's children on first expand, so a structural change (new/deleted/moved folder) isn't
    // reflected until the tree is rebuilt — hence Refresh and folder-creating operations call this. Same
    // whole-tree-reload simplification as the web client (the tree collapses).
    private async Task ReloadTreeAsync()
    {
        if (_api is null)
        {
            return;
        }

        var repositories = await _api.Documents.GetRepositoriesAsync();
        Tree.Clear();

        // The user's personal repository pinned above the shared ones, which are alphabetical (issue #339).
        // Composed by the SHARED rule (ADR 0689) rather than spelled out here, because the target pickers must
        // offer exactly these roots and were building their own list from GET /repositories alone — which
        // excludes the personal space, so it silently was not offerable.
        var personal = await _api.Profile.GetPersonalRepositoryAsync();
        foreach (var root in SimplArchive.Presentation.FilingRoots.Compose(personal, repositories, r => r.Name))
        {
            var repository = root.Node;
            // The personal space is always expandable — it holds at least the Intray + Check-out launcher nodes
            // (ADR "GUI-tree Personal space grouping"), even before any real subfolder exists — and its children
            // load through the loader that adds them.
            Tree.Add(root.Selectable
                ? new TreeNodeViewModel(repository.Id, repository.Name, repository.HasSubfolders, LoadTreeChildrenAsync, links: repository.Links, hasReferences: repository.HasReferences, hasChildren: repository.HasChildren, admits: repository.Admits, icon: repository.Icon,
                    canDelete: repository.CanDelete, canEditIndexData: repository.CanEditIndexData, canMove: repository.CanMove, canManagePermissions: repository.CanManagePermissions, canCreateChildren: repository.CanCreateChildren)
                : new TreeNodeViewModel(repository.Id, repository.Name, hasSubfolders: true, LoadPersonalChildrenAsync, links: repository.Links, isPersonal: true));
        }

        // Tenant admins get a synthetic "Administration → Users" branch (ADR "Tenant-admin Administration → Users
        // view") to browse every user's personal space; its children load from the admin endpoint.
        if (IsTenantAdmin)
        {
            Tree.Add(new TreeNodeViewModel(Guid.Empty, "Administration", true, LoadAdminRootAsync, syntheticIcon: "mdi-shield-account"));
        }

        // The roots are the only nodes anyone constructs directly; every descendant inherits the callback as it
        // loads, so a new place that creates child nodes cannot forget to wire it.
        // Remembering the tree's shape is its own responsibility and lives in its own class (#687-adjacent
        // size rule): this view-model only says which context it is in and hands over the roots.
        _treeMemory.Use(DesktopClientOptions.ApiBaseUrl, UserEmail);
        foreach (var root in Tree)
        {
            root.ExpansionChanged = _treeMemory.Record;
        }

        await _treeMemory.RestoreAsync(Tree);
    }

    // After a subfolder is created under parentId, refresh just that node's children in place + keep it expanded,
    // so the tree keeps showing the parent folder (whose contents are in the list pane) instead of collapsing to
    // the roots (ADR "Keep the desktop tree expanded on a structural change"). Falls back to a full rebuild only
    // if the parent isn't currently materialised in the tree (e.g. reached by drilling through the list pane).
    private async Task ShowNewChildInTreeAsync(Guid parentId)
    {
        if (FindTreeNode(Tree, parentId) is { } node)
        {
            await node.ReloadChildrenAsync();
        }
        else
        {
            await ReloadTreeAsync();
        }
    }

    private static TreeNodeViewModel? FindTreeNode(IEnumerable<TreeNodeViewModel> nodes, Guid id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id && !n.IsSynthetic && !n.IsLauncher)
            {
                return n;
            }
            if (FindTreeNode(n.Children, id) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private Task<IEnumerable<TreeNodeViewModel>> LoadAdminRootAsync(TreeNodeViewModel _) =>
        Task.FromResult<IEnumerable<TreeNodeViewModel>>(
            [new TreeNodeViewModel(Guid.Empty, "Users", true, LoadAdminUsersAsync, syntheticIcon: "mdi-account-group")]);

    private async Task<IEnumerable<TreeNodeViewModel>> LoadAdminUsersAsync(TreeNodeViewModel _)
    {
        var repos = await _api!.Admin.GetAdminPersonalRepositoriesAsync();
        // Each user's personal repo is a normal browsable node (Id = the repo; the admin's ACL bypass grants it).
        return repos.Select(r => new TreeNodeViewModel(
            r.RepositoryId,
            r.UserIsActive ? r.DisplayName : $"{r.DisplayName} (inactive)",
            r.HasSubfolders,
            LoadTreeChildrenAsync,
            isPersonal: true,
            hasChildren: r.HasChildren,
            // Carries `take-over` when this caller may perform it (ADR 0672) — absent otherwise, so the menu
            // item is simply not drawn rather than offering a button that answers 403.
            links: r.Links,
            canDelete: r.CanDelete, canEditIndexData: r.CanEditIndexData, canMove: r.CanMove, canManagePermissions: r.CanManagePermissions, canCreateChildren: r.CanCreateChildren));
    }

    // The Personal repository nests the Intray + Check-out launcher nodes above its real subfolders, mirroring
    // /SimplArchive/Personal (ADR "GUI-tree Personal space grouping"). Selecting a launcher switches to the matching
    // bottom tab (OnSelectedTreeNodeChanged), where the full staging / check-out UX lives.
    private async Task<IEnumerable<TreeNodeViewModel>> LoadPersonalChildrenAsync(TreeNodeViewModel node)
    {
        var launchers = new[]
        {
            new TreeNodeViewModel(Guid.Empty, "Intray", false, null, personalKind: "intray"),
            new TreeNodeViewModel(Guid.Empty, "Check-out", false, null, personalKind: "checkout"),
        };
        return launchers.Concat(await LoadTreeChildrenAsync(node));
    }

    private async Task<IEnumerable<TreeNodeViewModel>> LoadTreeChildrenAsync(TreeNodeViewModel node)
    {
        // The tree shows folders only — real child folders plus references whose target is a folder (a
        // shortcut node whose Id is the target folder, so it expands the target's subtree). See ADR
        // "Referenced folder in the tree".
        // Folders are always sorted alphabetically in the tree (issue #339) — the children endpoint orders by
        // creation for its cursor, so re-sort by name here (all pages are loaded).
        var children = await _api!.Documents.GetChildrenAsync(node.Href("children"));
        var folderNodes = children
            .Where(c => !c.HasVersions)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new TreeNodeViewModel(c.Id, c.Name, c.HasSubfolders, LoadTreeChildrenAsync, links: c.Links, hasReferences: c.HasReferences, hasChildren: c.HasChildren, admits: c.Admits, icon: c.Icon,
                canDelete: c.CanDelete, canEditIndexData: c.CanEditIndexData, canMove: c.CanMove, canManagePermissions: c.CanManagePermissions, canCreateChildren: c.CanCreateChildren));

        // Shortcuts, or none where the folder advertises none — see TreeReferenceNodes for why that is not the
        // same question as `children` above, and for the crash it stopped being (#735).
        var referenceNodes = await TreeReferenceNodes.ForAsync(node, _api.References, LoadTreeChildrenAsync);

        return folderNodes.Concat(referenceNodes);
    }
}
