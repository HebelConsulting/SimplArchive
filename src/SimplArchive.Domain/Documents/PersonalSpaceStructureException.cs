namespace SimplArchive.Domain.Documents;

/// <summary>
/// The personal space's first level was asked to hold something it does not (#634), or one of its provisioned
/// folders was deleted or moved out (#596).
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so the existing boundary catches keep working — every
/// DbContext invariant has always surfaced as that type. The point of the dedicated type is that the Api can
/// tell this refusal APART from the four others that share it.
/// </para>
/// <para>
/// Without it these came out as <c>DOCUMENT_NAME_CONFLICT</c>: <c>DocumentChildrenController</c> maps every
/// <see cref="InvalidOperationException"/> to a name clash, so refusing a file at the first level told the
/// caller <i>"a document with this name already exists"</i> — about a name that was a fresh GUID. The same
/// false cause <see cref="Masks.TypedFolderContainmentException"/> was created to stop, reappearing the moment
/// a new invariant threw the bare type.
/// </para>
/// </remarks>
public sealed class PersonalSpaceStructureException : InvalidOperationException
{
    private PersonalSpaceStructureException(string message)
        : base(message)
    {
    }

    /// <summary>Only the provisioned folders belong at the first level; put it in <c>My Documents</c>.</summary>
    public static PersonalSpaceStructureException NotAdmitted(string documentName) =>
        new($"'{documentName}' cannot be placed directly in the personal space — it holds only the folders it "
            + $"was provisioned with. Put it inside '{PersonalFolders.MyDocuments}' instead (#634).");

    /// <summary>A provisioned folder cannot be deleted — the surfaces that resolve against it would break.</summary>
    public static PersonalSpaceStructureException CannotDelete(string documentName) =>
        new($"'{documentName}' cannot be deleted: it is one of the personal space's standing folders, and the "
            + "calendar, contacts and mail views resolve against it (#596).");

    /// <summary>…nor moved out of the space, for the same reason.</summary>
    public static PersonalSpaceStructureException CannotMove(string documentName) =>
        new($"'{documentName}' cannot be moved out of the personal space: it is one of its standing folders, "
            + "and the calendar, contacts and mail views resolve against it (#596).");
}
