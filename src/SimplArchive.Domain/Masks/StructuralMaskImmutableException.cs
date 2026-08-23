namespace SimplArchive.Domain.Masks;

/// <summary>
/// A folder wearing a structural mask cannot be re-typed or un-typed (see
/// <see cref="WellKnownMaskIds.ImmutableStructuralMasks"/>).
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than a bare <see cref="InvalidOperationException"/>, and the reason is not tidiness:
/// <c>SaveChanges</c> throws that one type for several invariants, and every document-writing endpoint has a
/// <c>catch (InvalidOperationException)</c> that reports whatever cause it happens to assume. The mask endpoint
/// assumes a missing required field — so without this type, refusing to re-type a Mailbox would tell the user
/// that a required field was empty. That is a specific, checkable, false cause, which is worse than no message.
/// It is the same bug that produced DOCUMENT_NAME_CONFLICT about names that were fresh GUIDs, twice.
/// </para>
/// </remarks>
public sealed class StructuralMaskImmutableException : InvalidOperationException
{
    private StructuralMaskImmutableException(string message)
        : base(message)
    {
    }

    /// <summary>The folder's type is what makes its contents mean anything, so it is not a preference.</summary>
    public static StructuralMaskImmutableException CannotChange(string documentName, string maskName) =>
        new($"'{documentName}' is a '{maskName}' and cannot be given a different type. Its type is what makes "
            + "its contents reachable — mail arrives into it, or a notebook is projected from it — so changing "
            + "it would not reclassify the folder but strip the meaning from what is already inside. Move the "
            + "contents into an ordinary folder instead, or delete this one.");
}
