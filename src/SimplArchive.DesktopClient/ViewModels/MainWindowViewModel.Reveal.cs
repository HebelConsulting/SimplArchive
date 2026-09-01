namespace SimplArchive.DesktopClient.ViewModels;

// Making a target the user reached from SOMEWHERE ELSE current: a search result, and the tree walk it needs --
// expanding the ancestor chain, then revealing the folder or the document at the end of it.
//
// CLAUDE.md's shell principle already draws the line these depend on: REVEALING IS NOT NAVIGATING. Making a node
// visible and current is a different act from opening it, and conflating them means one click destroys the
// context the user was working in. That is the whole reason this is a subject rather than a few helpers.
//
// The web client has the same subject, and had it buried the same way -- its Go-to entry points sat under a
// heading reading "Rename / delete / recycle bin" until they moved to Home.Navigation. Both clients had put
// "open a thing that came from another surface" under a banner about something unrelated, which is worth
// knowing beyond the tidiness point: ADR 0511 treats the pair as one surface, and neither half was findable.
public sealed partial class MainWindowViewModel
{


    public async Task OpenSearchResultAsync(SearchResultViewModel result)
    {
        SelectedTab = 0;

        // Carry the search terms into the viewer so the hits highlight on the opened document (ADR "Search hit
        // overlay"). Only for a document result; a folder has no preview.
        if (!result.IsFolder)
        {
            Preview.FindQuery = Search.SearchQuery.Trim();
        }

        if (!result.IsFolder && result.Links?.GetValueOrDefault("parent") is { } parentHref
            && result.Links?.GetValueOrDefault("self") is { } docHref)
        {
            // Reveal the document in context: expand + select its parent folder in the tree, load the folder into
            // the list pane, and select the document there (issue #340). Both loads follow the hit's own
            // advertised addresses (#443); the tree expansion stays id-matching against rows already loaded.
            await RevealDocumentInTreeAsync(result.Id, docHref, parentHref);
        }
        else
        {
            // A folder, or a document filed at a repository root (itself a top-level tree node).
            await RevealFolderInTreeAsync(result.Links?.GetValueOrDefault("self")
                ?? throw new InvalidOperationException($"The search hit '{result.Name}' advertised no 'self' rel (ADR 0543)."));
        }
    }

    // Expands the tree along an ordered ancestor id chain (repository-root first), returning the last node — or null
    // if a link in the chain isn't in the visible tree (e.g. a reference-only path the tree doesn't mirror). The
    // repository roots are top-level Tree nodes carrying their real ids, and real subfolders nest by real id, so the
    // synthetic grouping nodes (Personal launchers / Administration, all Guid.Empty) never match a real ancestor.
    private async Task<TreeNodeViewModel?> ExpandTreePathAsync(IReadOnlyList<Guid> chain)
    {
        IReadOnlyList<TreeNodeViewModel> level = Tree;
        TreeNodeViewModel? node = null;
        foreach (var id in chain)
        {
            node = level.FirstOrDefault(n => n.Id == id);
            if (node is null)
            {
                return null;
            }

            await node.EnsureExpandedAsync();
            level = node.Children;
        }

        return node;
    }

    // Reveal a folder (or a root-level item): expand its ancestors, then select it in the tree so its contents load.
    // ONE fetch of the advertised address serves the id, the ancestors rel and the fallback open (ADR 0557).
    private async Task RevealFolderInTreeAsync(string folderSelfHref)
    {
        if (_api is null)
        {
            return;
        }

        var stub = await _api.GetDocumentByAddressAsync(folderSelfHref);
        var chain = await _api.Documents.GetAncestorsAsync(stub.Links["ancestors"]);
        chain.Add(stub.Id); // ancestors are up to the parent; append the folder itself as the reveal target
        var node = await ExpandTreePathAsync(chain);
        if (node is not null)
        {
            SelectedTreeNode = node; // OnSelectedTreeNodeChanged loads the folder's contents
        }
        else
        {
            // Not mirrored in the tree — fall back to a contents-only open of the already-fetched resource.
            await OpenLoadedFolderAsync(stub.Id, stub.Name, stub.Links, null);
        }
    }

    // Reveal a document: expand + select its parent folder in the tree, load the folder into the list, select the doc.
    private async Task RevealDocumentInTreeAsync(Guid documentId, string documentSelfHref, string parentHref)
    {
        if (_api is null)
        {
            return;
        }

        var node = await ExpandTreePathAsync(
            await _api.Documents.GetAncestorsAsync(await _api.Documents.RelViaSelfAsync(documentSelfHref, "ancestors")));

        // Load the parent folder into the list + select the document (+ its preview) regardless of the tree
        // outcome — following the caller's advertised addresses (#443).
        await OpenFolderAsync(parentHref, documentId);

        // Then reflect it in the tree — select the parent node without re-loading the folder (already loaded above).
        if (node is not null)
        {
            _suppressTreeSelectionLoad = true;
            SelectedTreeNode = node;
            _suppressTreeSelectionLoad = false;
        }
    }
}
