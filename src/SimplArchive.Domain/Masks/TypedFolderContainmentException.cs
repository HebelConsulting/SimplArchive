namespace SimplArchive.Domain.Masks;

/// <summary>
/// A typed folder was asked to hold something it does not admit, or a typed item to live somewhere that is not
/// its folder (<see cref="WellKnownMaskIds.TypedFolderPairs"/>).
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so the existing boundary catches keep working — the
/// DbContext's invariants have always surfaced as that type, and the Api translates it. The point of the
/// dedicated type is that a caller can now tell this refusal APART from the four other invariants that share
/// it: <c>DocumentChildrenController</c> mapped all of them to "a document with this name already exists",
/// so filing into an addressbook reported a name collision for a name that could not possibly collide.
/// </remarks>
public sealed class TypedFolderContainmentException : InvalidOperationException
{
    private TypedFolderContainmentException(string message)
        : base(message)
    {
    }

    /// <summary>The folder refuses the child: it admits only its own item type.</summary>
    public static TypedFolderContainmentException FolderAdmitsOnly(string documentName, TypedFolderPair pair) =>
        new($"'{documentName}' cannot live in a {pair.FolderName} — only {pair.ItemName}-masked documents can "
            + "(typed-folder containment, #562/#564).");

    /// <summary>The item refuses the folder: a typed item's primary location is only its own folder.</summary>
    public static TypedFolderContainmentException ItemBelongsIn(string documentName, TypedFolderPair pair) =>
        new($"'{documentName}' wears the {pair.ItemName} mask and can only live in a {pair.FolderName} "
            + "(typed-folder containment, #562/#564).");
}
