namespace SimplArchive.Presentation;

/// <summary>
/// One top-level place a user can browse or file into, and whether it may itself be the target.
/// </summary>
/// <param name="Node">The client's own node object — a view-model on one side, a record on the other.</param>
/// <param name="Selectable">
/// False for a personal space's ROOT: its first level is provisioned rather than user-filled (#634), so
/// nothing may be filed directly into it. It is still shown and still expands — what a user wants is one of
/// the folders inside it.
/// </param>
public sealed record FilingRoot<T>(T Node, bool Selectable);

/// <summary>
/// The top-level places a user can file into: their personal space first, then the shared repositories.
/// </summary>
/// <remarks>
/// <para>
/// The one answer the tree pane and every target picker must give identically — and did not. Both clients
/// built the tree's roots from two sources (the personal space, fetched from the <c>me</c> resource, then
/// <c>GET /repositories</c>, which deliberately excludes it) and built every PICKER's roots from the second
/// alone. So the Move dialog, the reference dialog and inbox filing offered the shared repositories and
/// silently omitted the user's own space: the one place a person is most likely to be filing into was the one
/// place they could not choose. The tree showed it the whole time, which is what made this look like a
/// permission problem rather than a missing root.
/// </para>
/// <para>
/// Same rule as ADR 0509 draws for the WebDAV mount, and for the same reason: a surface that claims to show
/// where things can go must show the same places the tree does, or the two become separate truths and only one
/// of them gets maintained. Composed here, once, so a fourth picker cannot repeat it.
/// </para>
/// <para>
/// What it does NOT cover is anything that is not a filing target: the tenant admin's synthetic Administration
/// branch, and the personal space's Intray / Check-out launchers. Those are places to LOOK, not places to put
/// something, and each tree adds its own.
/// </para>
/// </remarks>
public static class FilingRoots
{
    /// <summary>
    /// Composes the roots: the personal space pinned first, then the shared repositories by name.
    /// </summary>
    /// <param name="personal">The caller's personal space, or null when they have none.</param>
    /// <param name="shared">The shared repositories, in any order.</param>
    /// <param name="name">How to read a node's display name — the one thing that differs per client.</param>
    /// <remarks>
    /// Generic over the node type with a name selector rather than an interface implemented twice: the two
    /// clients' node types are an observable view-model and an immutable record, and the only thing this needs
    /// from either is its name.
    /// </remarks>
    public static List<FilingRoot<T>> Compose<T>(T? personal, IEnumerable<T> shared, Func<T, string> name)
        where T : class
    {
        // Alphabetical among the shared ones (issue #339); the personal space stays pinned above them, because
        // it is the one root that is always the same user's and always in the same place.
        var roots = shared
            .OrderBy(name, StringComparer.OrdinalIgnoreCase)
            .Select(node => new FilingRoot<T>(node, Selectable: true))
            .ToList();

        if (personal is not null)
        {
            roots.Insert(0, new FilingRoot<T>(personal, Selectable: false));
        }

        return roots;
    }
}
