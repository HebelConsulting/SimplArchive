using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The shortcut (reference) nodes a tree folder contributes, or none when it advertises no shortcuts (#735).
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than eight more lines in a 7,000-line view-model on the standing-debt list — and because
/// what it encodes is a rule, not a step: <c>children</c> is REQUIRED for a tree node (one that cannot be
/// expanded is a defect worth throwing over), while <c>references</c> is not. A missing rel means "not
/// available to you, here, now" (ADR 0543), which for shortcuts reads as <b>none are filed here</b>.
/// </para>
/// <para>
/// It threw instead, and on a path with no handler above it — so expanding a user under Administration → Users,
/// whose listing rows never carried the rel, killed the client outright. A listing that omits one rel should
/// cost that listing its shortcuts, not the whole application.
/// </para>
/// </remarks>
public static class TreeReferenceNodes
{
    /// <summary>Reads <paramref name="node"/>'s shortcuts, or answers empty when it advertises none.</summary>
    /// <param name="expand">How a reference's own children load — the target's subtree, not the shortcut's.</param>
    public static async Task<IEnumerable<TreeNodeViewModel>> ForAsync(
        TreeNodeViewModel node,
        ReferencesClient references,
        Func<TreeNodeViewModel, Task<IEnumerable<TreeNodeViewModel>>> expand)
    {
        if (!node.HasRel("references"))
        {
            return [];
        }

        // Folders only, alphabetical — the same shape the children half uses, and for the same reason: the
        // endpoint orders by creation for its cursor.
        //
        // A reference node's Id is the TARGET folder's, so expanding it walks the target's subtree and
        // selecting it lists the target (ADR "Referenced folder in the tree").
        //
        // It CARRIES THE ROW'S LINKS — the target's own `children`, `references` and `self`, which the server
        // advertises on every reference row unconditionally (#416). It was built from the target's id alone, and an
        // id has no address (ADR 0543): selecting any folder shortcut in the tree threw "No advertised address for
        // folder …", which the crash guard then reported as a lost connection. Reported on the flight school's
        // Aerodrome shortcuts in My Documents; every folder shortcut in the tree had it. The web client already
        // passed the row's links (BrowseService).
        return (await references.GetReferencesAsync(node.Href("references")))
            .Where(r => !r.HasVersions)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new TreeNodeViewModel(
                r.TargetId, r.Name, r.HasSubfolders, expand,
                isReference: true, hasReferences: r.HasReferences, hasChildren: r.HasChildren, links: r.Links));
    }
}
