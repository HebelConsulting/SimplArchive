using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// Where the caller's personal space is in the tree, and what its folders are called inside the WebDAV mount.
/// </summary>
/// <remarks>
/// Its own class rather than two more members on the shell view-model, which is on the standing-debt list and
/// may only shrink (issue #466) — and this is not the shell's job anyway: it is one question about the tree,
/// asked by the ribbon buttons and the Check-out tab.
///
/// A personal space is named after its OWNER (ADR 0671). The buttons used to spell out "Personal/…", which
/// addressed a folder that does not exist, so they silently did nothing.
/// </remarks>
internal static class PersonalSpaceTree
{
    /// <summary>The personal space's own name, or null before the tree has loaded.</summary>
    internal static string? NameIn(IEnumerable<TreeNodeViewModel> tree)
        => tree.FirstOrDefault(n => n.IsPersonal)?.Name;

    /// <summary>A folder inside it, as a path within the mount — empty when the name is not known yet.</summary>
    internal static string WebDavPath(IEnumerable<TreeNodeViewModel> tree, string leaf)
        => WebDavPaths.InPersonalSpace(NameIn(tree), leaf);
}
