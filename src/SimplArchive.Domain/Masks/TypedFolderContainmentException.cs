namespace SimplArchive.Domain.Masks;

/// <summary>
/// A typed folder was asked to hold something it does not admit, or a typed item to live somewhere that does
/// not admit it (<see cref="WellKnownMaskIds.TypedFolderRules"/>).
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

    /// <summary>The folder refuses the child: it admits only the masks it names.</summary>
    public static TypedFolderContainmentException FolderAdmitsOnly(string documentName, TypedFolderRule rule) =>
        new($"'{documentName}' cannot live in a {rule.FolderName} — only {rule.AdmittedNames} can "
            + "(typed-folder containment, #562/#564).");

    /// <summary>The folder is full: it admits this mask, but not another one of them (#596).</summary>
    /// <remarks>
    /// Distinct from <see cref="FolderAdmitsOnly"/> on purpose. "A personal space holds only one Mailbox" and
    /// "a personal space cannot hold a Mailbox" would send the reader looking for two different mistakes, and
    /// only one of them is real — the user's second mailbox is refused because they already have one, which is
    /// a fact they can act on.
    /// </remarks>
    public static TypedFolderContainmentException FolderAlreadyHolds(
        string documentName, ChildCardinalityRule rule) =>
        new($"'{documentName}' cannot be added: a {rule.FolderName} holds at most {rule.Max} "
            + $"{rule.ChildName}{(rule.Max == 1 ? string.Empty : "s")}, and one is already there "
            + "(child cardinality, #596).");

    /// <summary>
    /// The child refuses the folder: a typed item's primary location is only a folder that admits it. Plural
    /// because a Note lives in a Notebook OR a Section, and naming just one of them would send the reader to
    /// the wrong place half the time.
    /// </summary>
    public static TypedFolderContainmentException ItemBelongsIn(
        string documentName, Guid maskId, IReadOnlyList<TypedFolderRule> admittingRules)
    {
        var itemName = admittingRules
            .SelectMany(r => r.Admits)
            .First(a => a.MaskId == maskId).Name;
        var places = string.Join(" or ", admittingRules.Select(r => $"a {r.FolderName}"));
        return new($"'{documentName}' wears the {itemName} mask and can only live in {places} "
            + "(typed-folder containment, #562/#564).");
    }
}
