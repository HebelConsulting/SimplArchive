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

    /// <summary>
    /// Turns an id back into an address by FETCHING the resource and following its own <c>children</c> rel. The
    /// <c>api/documents/{id}</c> here is the one composition that cannot be avoided — it is the irreducible case
    /// of having an id and no resource — and it is deliberately the only one on this path.
    /// </summary>
    public async Task<string> FetchChildrenHrefAsync(Guid folderId)
    {
        var doc = await http.GetFromJsonAsync<DocumentLinksResponse>($"api/documents/{folderId}");
        return Links.Href(doc?.Links, "children")
            ?? throw new InvalidOperationException($"Document {folderId} advertised no 'children' rel (ADR 0543).");
    }

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
    public async Task<FolderContents> LoadContentsAsync(Guid folderId, Guid repositoryId, string? childrenHref = null)
    {
        var nodes = new List<BrowseNode>();
        var order = (FolderContentsSortOrder?)null;
        var url = childrenHref ?? await FetchChildrenHrefAsync(folderId);
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

        var refUrl = $"api/documents/{folderId}/references";
        while (refUrl is not null)
        {
            var page = await http.GetFromJsonAsync<ReferenceListResponse>(refUrl);
            foreach (var r in page?.References ?? [])
            {
                nodes.Add(new BrowseNode(r.Id, r.Name, r.HasChildren, r.HasVersions, r.HasSubfolders, r.HasReferences, true, r.ReferenceId, r.RealParentId, repositoryId,
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

        var attrs = node.HasVersions
            ? new Dictionary<string, object> { ["data-drop-doc"] = node.Id.ToString() }
            : new Dictionary<string, object> { ["data-drop-folder"] = node.Id.ToString() };

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
