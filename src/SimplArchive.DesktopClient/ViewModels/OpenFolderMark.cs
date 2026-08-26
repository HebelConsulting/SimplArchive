namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The tree's mark: which node is drawn as "you are here" (issue #686).
/// </summary>
/// <remarks>
/// <para>
/// The tree answers exactly one question — <em>where am I</em> — and the mark is how it answers. It follows the
/// folder the user has OPEN, never the row they have selected: the contents list already shows which of its own
/// rows is selected, so a second marker for that is a state competing with a state (ADR 0581), and it changed
/// meaning with the row type — a folder row moved it, a document row cleared it. Supersedes the behaviour
/// #696 shipped.
/// </para>
/// <para>
/// It cannot simply follow the tree's SELECTED node either. Drilling into a subfolder from the contents list,
/// or from a breadcrumb, moves the list without moving the tree's selection, so the folder the user is standing
/// in is frequently a child of the selected node rather than the node itself. That gap is exactly what a mark
/// is for: it says "here" without selecting, which would load contents and move them again.
/// </para>
/// <para>
/// Its own type rather than three more methods on the view-model, which is 7000 lines and on the standing debt
/// list (issue #517): a behaviour that needs explaining at this length has outgrown being a private helper.
/// </para>
/// </remarks>
internal static class OpenFolderMark
{
    /// <summary>Every node in the tree, depth-first — the mark can be anywhere in it.</summary>
    internal static IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Moves the mark to <paramref name="openFolderId"/>, clearing it everywhere else. Returns the node it
    /// landed on, or <c>null</c> when the loaded tree does not hold that folder.
    /// </summary>
    /// <remarks>
    /// Best-effort by design, and the failure is a CLEARED mark rather than a kept one: a folder the tree has
    /// not loaded is not marked, and nothing is marked rather than the previous folder being left standing. A
    /// stale mark is a claim about the wrong place (ADR 0559) — the same defect as a pane describing the
    /// subject before last.
    /// </remarks>
    internal static TreeNodeViewModel? Move(IEnumerable<TreeNodeViewModel> tree, Guid? openFolderId)
    {
        var nodes = Flatten(tree).ToList();
        foreach (var marked in nodes.Where(n => n.IsMarked))
        {
            marked.IsMarked = false;
        }

        if (openFolderId is not { } open || nodes.FirstOrDefault(n => n.Id == open) is not { } target)
        {
            return null;
        }

        target.IsMarked = true;
        return target;
    }

    /// <summary>
    /// The open folder as a row the detail pane can describe — what it shows when nothing is selected.
    /// </summary>
    /// <remarks>
    /// A folder is a Document with a mask, index fields and dates like any other (ADR 0200), so it gets the
    /// same pane rather than a thinner one. Built from the folder's OWN advertised links, which the contents
    /// load has already resolved — never from an id composed into an address (ADR 0543).
    /// </remarks>
    internal static NodeViewModel AsRow(Guid id, string? name, IReadOnlyDictionary<string, string>? links, bool hasChildren) => new()
    {
        Id = id,
        Name = name ?? string.Empty,
        Links = links,
        HasChildren = hasChildren,

        // A folder has no versions of its own to offer, whatever it contains.
        HasVersions = false,
    };
}
