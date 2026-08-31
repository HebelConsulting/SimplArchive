using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using MudBlazor;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// The repository tree's contents: its root nodes, and how a node's children are loaded.
/// </summary>
/// <remarks>
/// Held outside the pane component for the reason <see cref="SearchState"/> is (ADRs 0511/0558): the workbench
/// renders one tab at a time, so the tree pane is DISPOSED whenever the user visits Tasks or Search. Roots kept
/// in the component would be re-fetched on return, and — because MudTreeView keeps a node's loaded children in
/// the <see cref="TreeItemData{T}"/> itself — every expanded folder would collapse. Someone three levels deep
/// who glances at their tasks would come back to a closed tree, which is precisely the state a user is annoyed
/// to lose.
/// </remarks>
public sealed class TreeState(HttpClient http, ApiRoot apiRoot, BrowseService browse)
{
    /// <summary>The tree's top level: the personal repository, the shared ones, then the admin branch.</summary>
    public IReadOnlyCollection<TreeItemData<BrowseNode>> Roots { get; private set; } = [];

    /// <summary>True until the first <see cref="ReloadAsync"/> completes, so the pane can show a spinner.</summary>
    public bool Loading { get; private set; } = true;

    /// <param name="isTenantAdmin">
    /// Whether to append the synthetic Administration branch — a fact about the SIGNED-IN USER, which the shell
    /// resolves once from <c>/diagnostics/whoami</c>, so it is passed in rather than re-fetched here.
    /// </param>
    public async Task ReloadAsync(bool isTenantAdmin)
    {
        var reopen = ExpandedIds(Roots).ToList();
        try
        {
            var nodes = new List<TreeItemData<BrowseNode>>();

            // The user's personal repository, pinned at the top of the tree (ADR "Per-user personal repository").
            // POST is get-or-create; it's excluded from the shared GET /repositories list below.
            var personal = await browse.EnsurePersonalRepositoryAsync();

            var shared = new List<TreeItemData<BrowseNode>>();
            var url = await apiRoot.RequireAsync("repositories");
            while (url is not null)
            {
                var page = await http.GetFromJsonAsync<RepositoryListResponse>(url);
                foreach (var r in page?.Repositories ?? [])
                {
                    // A repository is its own repository scope.
                    shared.Add(BrowseService.ToTreeItem(new BrowseNode(r.Id, r.Name, r.HasChildren, r.HasVersions, r.HasSubfolders, RepositoryId: r.Id,
                CanDelete: r.CanDelete, CanEditIndexData: r.CanEditIndexData, CanMove: r.CanMove, CanManagePermissions: r.CanManagePermissions, CanCreateChildren: r.CanCreateChildren,
                Links: Links.RelMap(r.Links), Admits: r.Admits, Icon: r.Icon)));
                }
                url = Links.Href(page?.Links, "next");
            }

            // Personal pinned above the shared ones, which are alphabetical (issue #339) — composed by the
            // SHARED rule (ADR 0689) rather than spelled out here, because every target picker must offer
            // exactly these roots and each was building its own list from GET /repositories alone.
            nodes.AddRange(SimplArchive.Presentation.FilingRoots
                .Compose(personal is null ? null : PersonalTreeItem(personal), shared, n => n.Value!.DisplayName)
                .Select(root => root.Node));

            // Tenant admins get a synthetic "Administration → Users" branch (ADR "Tenant-admin Administration →
            // Users view") to browse every user's personal space; its children load from the admin endpoint.
            if (isTenantAdmin)
            {
                nodes.Add(new TreeItemData<BrowseNode>
                {
                    Value = new BrowseNode(Guid.Empty, "Administration", true, false, true, AdminKind: "admin-root"),
                    Expandable = true,
                    Text = "Administration",
                    Icon = Icons.Material.Filled.AdminPanelSettings,
                });
            }

            Roots = nodes;

            // Re-open what was open. A reload rebuilds from the ROOTS, so without this every refresh closes the
            // tree under the user — reported after a soft-delete: "the Demo Repository folder collapsed".
            //
            // Captured BEFORE the rebuild and replayed in breadth-first order, which is what makes it work at
            // any depth: a parent is always reopened before its child, so each child's siblings are loaded by
            // the time we look for it. RevealAsync cannot do this job — it searches only what is LOADED, and
            // immediately after a rebuild that is the roots alone, so it silently returns false for anything
            // deeper than one level.
            foreach (var id in reopen)
            {
                await ExpandAsync(id, includeSelf: true);
            }
        }
        catch (AccessTokenNotAvailableException) { }
        catch (HttpRequestException) { }
        finally { Loading = false; }
    }

    /// <summary>The tree contains folders only (no documents) — see ADR "Workbench pane content fixes".</summary>
    /// <summary>
    /// Makes <paramref name="id"/> visible in the tree: expands every ancestor on the path to it, loading
    /// children where a node has not been opened yet. Returns false when the node is not in the tree at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Revealing is NOT navigating (#686): this changes what the tree SHOWS, never what the list is listing.
    /// It is called when the user MOVES — so that the folder they are now standing in exists in the loaded tree
    /// and can be marked. Selecting a row no longer reveals anything: the tree answers "where am I", and
    /// selecting is not moving.
    /// </para>
    /// <para>
    /// Lives here rather than in the shell because the tree's SHAPE is this service's — it owns the roots, the
    /// expansion state and the lazy child loading. A shell that reached in to expand nodes would be a second
    /// place that knows how the tree is built.
    /// </para>
    /// <para>
    /// Only the loaded tree is searched. A folder reached by "Go to" may have ancestors the tree has never
    /// opened, and this returns false rather than walking the whole archive to find it — the honest answer for
    /// a node the tree does not have, and the caller simply leaves the highlight where it was.
    /// </para>
    /// </remarks>
    public async Task<bool> RevealAsync(Guid id, Guid? parentId = null)
    {
        // The parent first, and this is not an optimisation — it is what makes the search possible at all. A
        // node the tree has never expanded has no children loaded, so the target is not IN the loaded tree yet
        // and searching for it finds nothing. Opening the parent is what puts it there.
        if (parentId is { } parent && !await ExpandAsync(parent, includeSelf: true))
        {
            return false;
        }

        return await ExpandAsync(id, includeSelf: false);
    }

    // Expands the chain down to `id`; `includeSelf` also opens the node itself, loading its children.
    // Revealing a folder must not expand IT — that would be opening it — except when it is the parent whose
    // children we are about to look inside.
    private async Task<bool> ExpandAsync(Guid id, bool includeSelf)
    {
        var path = new List<ITreeItemData<BrowseNode>>();
        if (!FindPath(Roots, id, path))
        {
            return false;
        }

        var last = includeSelf ? path.Count : path.Count - 1;
        for (var i = 0; i < last; i++)
        {
            var node = path[i];
            node.Expanded = true;
            if (node.Children is null or { Count: 0 } && node.Value is { } value)
            {
                node.Children = [.. await LoadChildrenAsync(value)];
            }
        }

        return true;
    }

    // Every expanded node, BREADTH-FIRST — the order they must be reopened in, since expanding a node is what
    // loads the children the next one is found among.
    private static IEnumerable<Guid> ExpandedIds(IEnumerable<ITreeItemData<BrowseNode>> items)
    {
        var level = items.ToList();
        while (level.Count > 0)
        {
            var next = new List<ITreeItemData<BrowseNode>>();
            foreach (var item in level.Where(i => i.Expanded))
            {
                if (item.Value is { AdminKind: "" } value && value.Id != Guid.Empty)
                {
                    yield return value.Id;
                }

                if (item.Children is { } children)
                {
                    next.AddRange(children);
                }
            }

            level = next;
        }
    }

    // Depth-first, recording the chain so the caller can expand it. Searches only what is LOADED — an
    // unexpanded node's children are null, and fetching them here would turn "reveal" into a crawl of the
    // archive on every selection.
    private static bool FindPath(IEnumerable<ITreeItemData<BrowseNode>> items, Guid id, List<ITreeItemData<BrowseNode>> path)
    {
        foreach (var item in items)
        {
            path.Add(item);
            if (item.Value?.Id == id || (item.Children is { } children && FindPath(children, id, path)))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    public async Task<IReadOnlyCollection<TreeItemData<BrowseNode>>> LoadChildrenAsync(BrowseNode node)
    {
        if (node.AdminKind is not "")
        {
            return await LoadAdminChildrenAsync(node);
        }


        // Folders are always sorted alphabetically in the tree (issue #339); the contents load orders for its list
        // default, so re-sort the folder nodes by name here.
        var children = (await browse.LoadContentsAsync(node.Id, node.RepositoryId, BrowseService.ChildrenHrefOf(node), BrowseService.ReferencesHrefOf(node))).Nodes
            .Where(c => !c.HasVersions)
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).Select(BrowseService.ToTreeItem).ToList();

        // The Personal repository nests the Intray + Check-out launcher nodes above its real subfolders,
        // mirroring /webdav/Personal (ADR "GUI-tree Personal space grouping"). Clicking a launcher switches to
        // the corresponding bottom tab, where the full staging / check-out UX lives.
        if (node.PersonalKind == "personal-root")
        {
            children.Insert(0, PersonalLauncherItem("checkout", "Check-out", Icons.Material.Filled.LockOpen));
            children.Insert(0, PersonalLauncherItem("intray", "Intray", Icons.Material.Filled.Inbox));
        }

        return children;
    }

    // Loads the synthetic Administration branch's children (ADR "Tenant-admin Administration → Users view").
    private async Task<IReadOnlyCollection<TreeItemData<BrowseNode>>> LoadAdminChildrenAsync(BrowseNode node)
    {
        if (node.AdminKind == "admin-root")
        {
            return
            [
                new TreeItemData<BrowseNode>
                {
                    Value = new BrowseNode(Guid.Empty, "Users", true, false, true, AdminKind: "admin-users"),
                    Expandable = true,
                    Text = "Users",
                    Icon = Icons.Material.Filled.People,
                },
            ];
        }

        // admin-users → one node per user's personal repository (browsable via the admin's ACL bypass). Reached
        // by following root → `admin` → `personal-repositories` — the admin index exists precisely so this
        // listing has a rel to follow (#416); the ledger's old claim that it needed a server change was stale.
        // A fetch error returns an empty branch rather than breaking the tree.
        AdminPersonalReposResponse? page;
        try
        {
            var admin = await http.GetFromJsonAsync<AdminIndexResponse>(await apiRoot.RequireAsync("admin"));
            page = Links.Href(admin?.Links, "personal-repositories") is { } href
                ? await http.GetFromJsonAsync<AdminPersonalReposResponse>(href)
                : null;
        }
        catch (Exception) { page = null; }
        return (page?.Repositories ?? []).Select(r => new TreeItemData<BrowseNode>
        {
            Value = new BrowseNode(r.RepositoryId, r.DisplayName, r.HasChildren, false, r.HasSubfolders, RepositoryId: r.RepositoryId,
                CanDelete: r.CanDelete, CanEditIndexData: r.CanEditIndexData, CanMove: r.CanMove, CanManagePermissions: r.CanManagePermissions, CanCreateChildren: r.CanCreateChildren,
                Links: r.Links.ToDictionary(l => l.Rel, l => l.Href)),
            Expandable = r.HasSubfolders,
            Text = r.UserIsActive ? r.DisplayName : $"{r.DisplayName} (inactive)",
            Icon = MaskIcon.Filled("person")!,
        }).ToList();
    }

    // The personal repository's tree node carries a distinct person icon; it browses like any repository (its own
    // RepositoryId), so drill-in/upload/recycle-bin all flow through the existing paths.
    private static TreeItemData<BrowseNode> PersonalTreeItem(PersonalRepositoryResponse personal) => new()
    {
        Value = new BrowseNode(personal.Id, personal.Name, personal.HasChildren, false, personal.HasSubfolders, RepositoryId: personal.Id, PersonalKind: "personal-root"),
        // Always expandable — it holds at least the Intray + Check-out launcher nodes (ADR "GUI-tree Personal
        // space grouping"), even before any real subfolder exists.
        Expandable = true,
        Text = personal.Name,
        Icon = MaskIcon.Filled("person")!,
    };

    private static TreeItemData<BrowseNode> PersonalLauncherItem(string kind, string text, string icon) => new()
    {
        Value = new BrowseNode(Guid.Empty, text, false, false, false, PersonalKind: kind),
        Expandable = false,
        Text = text,
        Icon = icon,
    };


    private record AdminPersonalReposResponse { public List<AdminPersonalRepo> Repositories { get; set; } = []; }

    /// <summary>The admin index — an entry-point resource whose rels are what it exists to advertise.</summary>
    private record AdminIndexResponse { public List<LinkResponse> Links { get; set; } = []; }

    private record AdminPersonalRepo
    {
        // The three answers this row carries so the tree's context menu can gate on them (#858). A tree node
        // gets Rename / Move to / Delete, so an admin listing that omitted them would hide those on every
        // personal space while the other listings offered them correctly — the "present on three of six
        // surfaces" defect #638 exists to prevent.
        public bool CanDelete { get; set; }

        public bool CanEditIndexData { get; set; }

        public bool CanMove { get; set; }

        // The admin personal-repositories listing stamps this like any other row (#854); a personal space's
        // ROOT never admits a plain child, so it arrives false — which is the answer, not an omission.
        public bool CanCreateChildren { get; set; }

        public bool CanManagePermissions { get; set; }

        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public bool UserIsActive { get; set; }
        public Guid RepositoryId { get; set; }
        public bool HasChildren { get; set; }
        public bool HasSubfolders { get; set; }

        // The row's own addresses, including `take-over` when this caller may perform it (ADR 0672). A missing
        // rel is the server saying "not available to you, here, now", so the menu item simply is not drawn.
        public List<LinkResponse> Links { get; set; } = [];
    }
}
