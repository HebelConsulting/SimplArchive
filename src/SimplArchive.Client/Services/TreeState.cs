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
        try
        {
            var nodes = new List<TreeItemData<BrowseNode>>();

            // The user's personal repository, pinned at the top of the tree (ADR "Per-user personal repository").
            // POST is get-or-create; it's excluded from the shared GET /repositories list below.
            var personal = await browse.EnsurePersonalRepositoryAsync();
            if (personal is not null)
            {
                nodes.Add(PersonalTreeItem(personal));
            }

            var shared = new List<TreeItemData<BrowseNode>>();
            var url = await apiRoot.RequireAsync("repositories");
            while (url is not null)
            {
                var page = await http.GetFromJsonAsync<RepositoryListResponse>(url);
                foreach (var r in page?.Repositories ?? [])
                {
                    // A repository is its own repository scope.
                    shared.Add(BrowseService.ToTreeItem(new BrowseNode(r.Id, r.Name, r.HasChildren, r.HasVersions, r.HasSubfolders, RepositoryId: r.Id, Links: Links.RelMap(r.Links))));
                }
                url = Links.Href(page?.Links, "next");
            }

            // Shared repositories sorted alphabetically (issue #339); Personal stays pinned above them.
            nodes.AddRange(shared.OrderBy(n => n.Value!.DisplayName, StringComparer.OrdinalIgnoreCase));

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
        }
        catch (AccessTokenNotAvailableException) { }
        catch (HttpRequestException) { }
        finally { Loading = false; }
    }

    /// <summary>The tree contains folders only (no documents) — see ADR "Workbench pane content fixes".</summary>
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
            Value = new BrowseNode(r.RepositoryId, r.DisplayName, r.HasChildren, false, r.HasSubfolders, RepositoryId: r.RepositoryId),
            Expandable = r.HasSubfolders,
            Text = r.UserIsActive ? r.DisplayName : $"{r.DisplayName} (inactive)",
            Icon = Icons.Material.Filled.Person,
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
        Icon = Icons.Material.Filled.Person,
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
        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public bool UserIsActive { get; set; }
        public Guid RepositoryId { get; set; }
        public bool HasChildren { get; set; }
        public bool HasSubfolders { get; set; }
    }
}
