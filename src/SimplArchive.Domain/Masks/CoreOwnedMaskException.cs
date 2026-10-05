namespace SimplArchive.Domain.Masks;

/// <summary>
/// A core-owned mask (<see cref="WellKnownMaskIds.CoreOwnedMasks"/> — the manuals folder's "SimplArchive
/// Documentation") was assigned, changed, removed or duplicated by something other than the core (ADR 0891).
/// </summary>
/// <remarks>
/// Its own type for the reason <see cref="StructuralMaskImmutableException"/> gives: SaveChanges throws
/// <see cref="InvalidOperationException"/> for several invariants, and a caller catching that type wholesale
/// would report a cause it merely assumed. The core finds the manuals folder BY this mask, so each refusal below
/// protects the same fact — there is exactly one such folder, and the core made it.
/// </remarks>
public sealed class CoreOwnedMaskException : InvalidOperationException
{
    private CoreOwnedMaskException(string message)
        : base(message)
    {
    }

    /// <summary>Something other than the core tried to put the mask on a document.</summary>
    public static CoreOwnedMaskException NotAssignable(string documentName, string maskName) =>
        new($"'{documentName}' cannot be given the '{maskName}' type: only SimplArchive itself assigns it, to the "
            + "one folder it files manuals into. Use an ordinary folder; a manual inside one may wear the 'Manual' type.");

    /// <summary>The folder that wears it may not lose it or be re-typed.</summary>
    public static CoreOwnedMaskException CannotChange(string documentName, string maskName) =>
        new($"'{documentName}' is the '{maskName}' folder SimplArchive files its manuals into, and its type cannot "
            + "be changed or removed — SimplArchive finds the folder by that type, and would otherwise create a second one.");

    /// <summary>A second live document would make "the manuals folder" ambiguous.</summary>
    public static CoreOwnedMaskException OnlyOne(string documentName, string maskName) =>
        new($"'{documentName}' cannot wear the '{maskName}' type: another folder in this tenant already does, and "
            + "there is only ever one.");
}
