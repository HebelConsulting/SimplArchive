using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using MudBlazor;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// The browsing plumbing the tree pane and the contents list both need: reading a folder's contents, turning a
/// node into a tree item, choosing its glyph, and describing it as a drag/drop participant.
/// </summary>
/// <remarks>
/// Extracted ahead of the panes themselves (ADR 0558): the two of them SHARE this, and pulling one pane out
/// first would have left the other reaching back into the page for it — or, worse, copying it. A copy is what
/// this codebase has repeatedly paid for: the fourth one gets the fix and the first three do not.
/// </remarks>
public sealed class BrowseService(HttpClient http, ApiRoot apiRoot)
{
    /// <summary>Shared empty attribute set, so a non-participating node costs no allocation per render.</summary>
    public static readonly Dictionary<string, object> EmptyAttributes = new();

    /// <summary>The row's advertised children address, or <c>null</c> when the listing carried none.</summary>
    public static string? ChildrenHrefOf(BrowseNode node) =>
        node.Links is not null && node.Links.TryGetValue("children", out var href) ? href : null;

    /// <summary>The row's advertised references address — travels with children so opening a folder reads both
    /// collections from the addresses the row carried rather than fetching to learn one of them (ADR 0557).</summary>
    public static string? ReferencesHrefOf(BrowseNode node) =>
        node.Links is not null && node.Links.TryGetValue("references", out var href) ? href : null;

    /// <summary>
    /// Turns an id back into an address by FETCHING the resource and following its own <paramref name="rel"/>.
    /// The <c>api/documents/{id}</c> here is the one composition that cannot be avoided — the irreducible case
    /// of holding an id and no resource — and it is deliberately the ONLY place the client writes it.
    /// </summary>
    /// <remarks>
    /// One implementation for every rel, not one per rel. It was written twice — here for <c>children</c> when
    /// opening a folder, and again in the workbench page for <c>subscription</c> and <c>archive-entries</c> —
    /// which is two copies of the client's single most sensitive line: the one address it is allowed to build.
    /// A second copy is how a rule with one exception quietly acquires a second.
    ///
    /// Deliberately not solved by advertising these rels on the listings. A listing advertises what BROWSING
    /// needs; following a folder or opening a zip is an action taken on ONE row, occasionally. Carrying them on
    /// every row of every page to serve that would invert the cost (ADR 0557, #416).
    /// </remarks>
    public async Task<string> FetchRelAsync(Guid documentId, string rel)
    {
        var doc = await FetchAsync(documentId);
        return Links.Href(doc?.Links, rel)
            ?? throw new InvalidOperationException($"Document {documentId} advertised no '{rel}' rel (ADR 0543).");
    }

    /// <summary>
    /// The resource behind an id — ONE read whose rels the caller may then follow several of (ADR 0557), where
    /// <see cref="FetchRelAsync"/> answers for exactly one. This is the only method that composes the address.
    /// </summary>
    public Task<DocumentLinksResponse?> FetchAsync(Guid documentId) =>
        http.GetFromJsonAsync<DocumentLinksResponse>($"api/documents/{documentId}");

    /// <summary>A folder's children address, resolved from its id — see <see cref="FetchRelAsync"/>.</summary>
    public Task<string> FetchChildrenHrefAsync(Guid folderId) => FetchRelAsync(folderId, "children");

    /// <summary>
    /// A folder's real children plus the references (shortcuts) filed in it, and the order the folder wants them
    /// listed in.
    /// </summary>
    /// <param name="childrenHref">
    /// The address the caller already holds, from the row it is acting on. When <c>null</c> — a synthetic node,
    /// or a caller that only knows an id — the resource is FETCHED and its own <c>children</c> rel followed: one
    /// round trip, never a composed sub-resource path (ADR 0543, issue #416).
    /// </param>
    /// <remarks>
    /// The sort order is RETURNED rather than written to a caller's field, which is what lets one loader serve
    /// both panes: the tree ignores it, the contents list adopts it as its default. It used to be a
    /// <c>trackAsCurrentFolder</c> flag that reached back into the page's state — not something a service can do,
    /// and not something a reader could see at the call site.
    /// </remarks>
    public async Task<FolderContents> LoadContentsAsync(Guid folderId, Guid repositoryId, string? childrenHref = null, string? referencesHref = null)
    {
        // A caller holding the row passes BOTH addresses it advertises; a caller holding only an id costs one
        // fetch for the two of them together — never a fetch per rel (ADR 0557).
        if (childrenHref is null || referencesHref is null)
        {
            var doc = await FetchAsync(folderId);
            childrenHref ??= Links.Href(doc?.Links, "children")
                ?? throw new InvalidOperationException($"Document {folderId} advertised no 'children' rel (ADR 0543).");
            referencesHref ??= Links.Href(doc?.Links, "references");
        }

        var nodes = new List<BrowseNode>();
        var order = (FolderContentsSortOrder?)null;
        var url = childrenHref;
        while (url is not null)
        {
            var page = await http.GetFromJsonAsync<DocumentChildrenResponse>(url);
            order ??= page?.ContentsSortOrder;
            foreach (var c in page?.Children ?? [])
            {
                nodes.Add(new BrowseNode(c.Id, c.Name, c.HasChildren, c.HasVersions, c.HasSubfolders, c.HasReferences, RepositoryId: repositoryId, FileExtension: c.FileExtension, OnLegalHold: c.OnLegalHold,
                    CheckedOut: c.CheckedOut, CheckedOutByMe: c.CheckedOutByMe, CheckedOutByName: c.CheckedOutByName,
                    DocumentType: c.DocumentType, DocumentDate: c.DocumentDate, SizeBytes: c.SizeBytes, Tags: c.Tags, SensitivityLabelName: c.SensitivityLabelName, SensitivityLabelColor: c.SensitivityLabelColor, VersionCount: c.VersionCount, VersionCreatedAt: c.VersionCreatedAt,
                    ChatHref: Links.Href(c.Links, "chat"),
                    Links: Links.RelMap(c.Links)));
            }
            url = Links.Href(page?.Links, "next");
        }

        var refUrl = referencesHref;
        while (refUrl is not null)
        {
            var page = await http.GetFromJsonAsync<ReferenceListResponse>(refUrl);
            foreach (var r in page?.References ?? [])
            {
                nodes.Add(new BrowseNode(r.Id, r.Name, r.HasChildren, r.HasVersions, r.HasSubfolders, r.HasReferences, true, r.ReferenceId, r.RealParentId, repositoryId,
                    ChatHref: Links.Href(r.Links, "chat"), // reference rows now carry the target's sub-resources
                    Links: Links.RelMap(r.Links)));
            }
            refUrl = Links.Href(page?.Links, "next");
        }

        return new FolderContents(nodes, order);
    }

    /// <summary>
    /// Get-or-create the current user's personal repository. <c>null</c> on failure (e.g. a not-yet-ready
    /// session) so the tree still renders the shared repositories.
    /// </summary>
    public async Task<PersonalRepositoryResponse?> EnsurePersonalRepositoryAsync()
    {
        try
        {
            var response = await http.PostAsync(await apiRoot.RequireMeAsync("personalRepository"), null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<PersonalRepositoryResponse>();
        }
        catch (AccessTokenNotAvailableException) { return null; }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Material icon per node — a shortcut variant for references, matching the desktop client's icons.</summary>
    public static string NodeIcon(BrowseNode n) => (n.IsReference, n.IsFolder) switch
    {
        (true, true) => Icons.Material.Filled.FolderSpecial,
        (true, false) => Icons.Material.Filled.Link,
        (false, true) => Icons.Material.Filled.Folder,
        (false, false) => Icons.Material.Filled.Description,
    };

    /// <summary>
    /// The TREE's icon variant: an empty folder takes the OUTLINE glyph, so "nothing here" is carried by the
    /// shape and not by colour alone — readable to someone who can't tell the two golds apart, and at any
    /// contrast setting (ADR "Folder icon scheme").
    /// </summary>
    /// <remarks>
    /// A referenced folder keeps its shortcut glyph in outline form rather than flattening to a plain folder:
    /// being empty must not cost the node what it IS. The contents list keeps the filled glyph throughout —
    /// emptiness is a property of a folder you might drill INTO, which is the tree's job.
    /// </remarks>
    public static string TreeIcon(BrowseNode n) => (n.IsEmptyFolder, n.IsReference) switch
    {
        (false, _) => NodeIcon(n),
        (true, true) => Icons.Material.Outlined.FolderSpecial,
        (true, false) => Icons.Material.Outlined.Folder,
    };

    public static TreeItemData<BrowseNode> ToTreeItem(BrowseNode node) => new()
    {
        Value = node,
        // The tree is folders-only, so the caret reflects sub-folders (not any child) — a folder holding
        // only documents is a leaf in the tree. See ADR "Workbench pane content fixes".
        Expandable = node.HasSubfolders,
        Text = node.Name,
        Icon = TreeIcon(node),
    };

    /// <summary>
    /// A folder row is a drop target that files into itself; a document row is a drop target that opens the
    /// inbox-style filing dialog (ADR "List-pane drop filing"). For a reference, <c>node.Id</c> is the target's.
    /// </summary>
    public static Dictionary<string, object> DropAttributes(BrowseNode? node)
    {
        if (node is null)
        {
            return EmptyAttributes;
        }

        // The Personal ▸ Inbox / Check-out launcher nodes are SYNTHETIC — Guid.Empty, no folder behind them — so
        // the folder branch below was handing them data-drop-folder="00000000-…", a target whose every drop
        // 404s. An inert drop zone is worse than none: the user concludes the feature is broken rather than
        // absent (ADR 0543 applied to the affordance itself, #467).
        //
        // Inbox gets its own attribute and a real path. Check-out advertises NOTHING until its semantics are
        // settled — a drop there means "add a stash", which is only meaningful for a document this user already
        // has checked out, and the launcher node carries no document.
        var attrs = node.PersonalKind switch
        {
            // The launcher nodes ARE the drop targets, and each names the tab its result lives in — a drop
            // files the documents and then opens that tab, so the user sees what happened (#467). The tree
            // itself shows folders, so neither launcher lists documents underneath it.
            "inbox" => new Dictionary<string, object> { ["data-drop-inbox"] = "true" },
            // Check-out takes a file from the COMPUTER only, and only when a document of that very name is
            // already checked out by this user: the round trip is download → edit offline → drag back, and the
            // filename is what says which document the working copy belongs to. Dragging a document from the
            // LIST here is a no-op — it is not an internal drag target (see below).
            "checkout" => new Dictionary<string, object> { ["data-drop-checkout"] = "true" },
            _ when node.HasVersions => new Dictionary<string, object> { ["data-drop-doc"] = node.Id.ToString() },
            _ => new Dictionary<string, object> { ["data-drop-folder"] = node.Id.ToString() },
        };

        // A real document/folder is also an internal drag source — dragging it onto a folder moves or references
        // it (mirrors the desktop client, ADR "Desktop drag-and-drop move and reference"). Synthetic admin /
        // personal-space / launcher nodes aren't movable, so they're not drag sources.
        if (node is { AdminKind: "", PersonalKind: "" })
        {
            attrs["draggable"] = "true";
            attrs["data-node-id"] = node.Id.ToString();
            attrs["data-node-ref"] = node.IsReference ? "true" : "false";
        }

        return attrs;
    }
}

/// <summary>
/// What one folder listing yielded: its rows, and the order it wants them in (<c>null</c> when the listing
/// returned no page at all).
/// </summary>
public sealed record FolderContents(List<BrowseNode> Nodes, FolderContentsSortOrder? SortOrder);
