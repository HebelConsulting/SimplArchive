namespace SimplArchive.Domain.Masks;

/// <summary>
/// A typed folder was asked to hold something it does not admit, or a typed item to live somewhere that does
/// not admit it.
/// </summary>
/// <remarks>
/// <para>
/// The rules are read from the MODEL since ADR 0655 (<c>MaskContainmentRules</c>), so every factory here takes
/// NAMES rather than the static rule records it used to. The static tables in <see cref="WellKnownMaskIds"/>
/// survive as the SEED for the well-known masks, not as what the invariant consults.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so the existing boundary catches keep working — the
/// DbContext's invariants have always surfaced as that type, and the Api translates it. The point of the
/// dedicated type is that a caller can now tell this refusal APART from the four other invariants that share
/// it: <c>DocumentChildrenController</c> mapped all of them to "a document with this name already exists",
/// so filing into an addressbook reported a name collision for a name that could not possibly collide.
/// </para>
/// </remarks>
public sealed class TypedFolderContainmentException : InvalidOperationException
{
    private TypedFolderContainmentException(string message)
        : base(message)
    {
    }

    /// <summary>The folder refuses the child: it admits only the masks it names.</summary>
    /// <remarks>
    /// <para>
    /// Takes NAMES rather than a <see cref="TypedFolderRule"/> since ADR 0655: the rules are read from the
    /// model now, so the names come from each mask's current version. That is a fix as well as a refactor — a
    /// renamed mask used to be refused under its old name, because the name was baked into the static table.
    /// </para>
    /// <para>
    /// <b>The listed set is what the folder REALLY admits</b>, which is wider than what this message used to
    /// say. A Mailbox admits ordinary folders, and the old message — built from the two-directional table
    /// alone — never mentioned it, so a user told "only IMAP Special or Notebook can live here" could go and
    /// successfully create a folder there. A refusal that misdescribes the rule is worse than a terse one.
    /// </para>
    /// </remarks>
    public static TypedFolderContainmentException FolderAdmitsOnly(
        string documentName, string folderName, IReadOnlyList<string> admittedNames) =>
        new($"'{documentName}' cannot live in a {folderName} — only {Listed(admittedNames)} can "
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

    /// <summary>The folder holds items, never other folders (#596).</summary>
    /// <remarks>
    /// Its own message because the reason is neither admission nor capacity: an ephemeral folder may hold any
    /// number of messages, and refuses a folder specifically. Saying "only eMail can live here" would be a
    /// lie the moment a second item mask arrives.
    /// </remarks>
    public static TypedFolderContainmentException FolderHoldsNoSubfolders(string documentName, string folderName) =>
        new($"'{documentName}' cannot be created in a {folderName}: it holds messages, not folders. "
            + "A folder there would be part of the archive while its parent is not (#596).");

    /// <summary>
    /// The child refuses the folder: a typed item's primary location is only a folder that admits it. Plural
    /// because a Note lives in a Notebook OR a Section, and naming just one of them would send the reader to
    /// the wrong place half the time.
    /// </summary>
    public static TypedFolderContainmentException ItemBelongsIn(
        string documentName, string itemName, IReadOnlyList<string> folderNames) =>
        new($"'{documentName}' wears the {itemName} mask and can only live in "
            + $"{Listed(folderNames.Select(n => $"a {n}").ToList())} (typed-folder containment, #562/#564).");

    // "A or B", "A, B or C" — the shape the static rules produced when they joined on " or ", kept so a
    // two-element list reads exactly as it always did and only longer ones gain a comma.
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count <= 1
            ? names.FirstOrDefault() ?? string.Empty
            : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}";
}
