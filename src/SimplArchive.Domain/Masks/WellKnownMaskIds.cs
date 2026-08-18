namespace SimplArchive.Domain.Masks;

// Fixed, cross-tenant Mask.Id values — every tenant's "Basic Entry"/"Folder"/"eMail" mask shares the exact
// same Id, matching how a real DMS ships a fixed set of default document-type identifiers rather than
// each tenant getting its own randomly-generated ones. See ADR "Mask composite primary key for cross-tenant
// well-known IDs" and ADR "Mask creation endpoint".
public static class WellKnownMaskIds
{
    public static readonly Guid BasicEntry = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E30");

    public static readonly Guid Folder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E31");

    /// <summary>The mask a REPOSITORY wears — a document with no parent (ADR 0627, #596).</summary>
    /// <remarks>
    /// <para>
    /// Identical to <see cref="Folder"/> in shape; it differs in name and id so that a repository can be
    /// recognised as one. ADR 0200 already defines a repository positionally — <c>ParentId == null</c> — and
    /// this does not replace that. The two are kept in LOCKSTEP, enforced both ways in
    /// <c>SaveChanges</c>: a document wearing this mask must be a root, and a root must wear this mask unless
    /// it is a personal space (which wears <see cref="UserFolder"/>, ADR 0590).
    /// </para>
    /// <para>
    /// Lockstep is what makes the duplication safe. Two representations of one fact can normally disagree —
    /// which is the objection to storing a derived value — so the invariant removes the possibility rather
    /// than trusting callers. What it buys is that <c>documentType</c> says "Repository" in a listing and over
    /// IMAP with no extra query, which is the whole reason it exists.
    /// </para>
    /// </remarks>
    public static readonly Guid Repository = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3D");

    public static readonly Guid EMail = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E32");

    // The mask a per-user personal space wears (ADR 0590). A personal repository is not a plain folder: it is
    // somebody's, and the metadata that belongs on it — a telephone number, an address to reach them at — has
    // nowhere to live on the fieldless Folder mask.
    public static readonly Guid UserFolder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E35");

    // The Notebook family (#562 slice 5, ADR "IMAP endpoint: Notes"; sections added by #564). A Notebook is a
    // TYPED folder, but unlike an Addressbook or a Calendar it admits TWO masks: notes, and sections that hold
    // more of the same. That is not a special case bolted on — Apple Notes sorts notes into subfolders, so a
    // flat notebook cannot represent what the client already does.
    //
    // The id is unchanged through the "Note Folder" → "Notebook" rename, so no document moves: only the
    // display name heals, exactly as Addressbook/Calendar did.
    public static readonly Guid Notebook = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E36");

    public static readonly Guid Note = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E37");

    // The CalDAV/CardDAV pairs (#564, ADR 0619): an Addressbook admits only Contact-masked children, a
    // Calendar only Appointment-masked ones — the same containment as the Notes pair above, and unlike
    // Notes these folders may sit ANYWHERE in the archive tree, each subscribable where the ACL allows.
    // Named for what a user calls them rather than for their role in the model: the item of a Calendar is an
    // Appointment (DE Termin), mirroring Addressbook → Contact, and no mask carries a "Folder" suffix.
    public static readonly Guid Addressbook = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E38");

    public static readonly Guid Calendar = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E39");

    public static readonly Guid Contact = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3A");

    public static readonly Guid Appointment = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3B");

    /// <summary>A section INSIDE a notebook: a folder that holds notes and further sections (#564).</summary>
    /// <remarks>
    /// Fieldless, like the Notebook it lives in — it types the folder, and the fields live on the notes.
    /// </remarks>
    public static readonly Guid NotebookSection = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3C");

    /// <summary>
    /// The typed-folder rules, as data: a folder mask admits ONLY children wearing one of its admitted masks,
    /// and an admitted mask's primary location is ONLY a folder that admits it (references may point
    /// anywhere). One table rather than a copy of the rule per family.
    /// </summary>
    /// <remarks>
    /// This was a list of PAIRS — one folder, one item — until sections arrived. Two things broke that shape at
    /// once: a Notebook admits two masks, and a NotebookSection admits ITSELF, so the relation is neither
    /// one-to-one nor acyclic. Rather than special-case notebooks, admission is now a SET and every family
    /// reads the same way; Addressbook and Calendar simply have sets of one.
    /// </remarks>
    public static readonly IReadOnlyList<TypedFolderRule> TypedFolderRules =
    [
        new(Notebook, "Notebook", [(NotebookSection, "Section"), (Note, "Note")]),
        new(NotebookSection, "Section", [(NotebookSection, "Section"), (Note, "Note")]),
        new(Addressbook, "Addressbook", [(Contact, "Contact")]),
        new(Calendar, "Calendar", [(Appointment, "Appointment")]),
    ];

    /// <summary>The masks that may only ever live inside a typed folder, with the folders that admit each.</summary>
    /// <remarks>
    /// Derived from <see cref="TypedFolderRules"/> rather than written out again: a second hand-maintained
    /// table is how the two directions of one rule drift apart, and the drift is invisible until something is
    /// filed where it should not be.
    /// </remarks>
    public static readonly IReadOnlyDictionary<Guid, IReadOnlyList<TypedFolderRule>> AdmittingFolders =
        TypedFolderRules
            .SelectMany(rule => rule.Admits.Select(a => (a.MaskId, Rule: rule)))
            .GroupBy(x => x.MaskId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TypedFolderRule>)[.. g.Select(x => x.Rule)]);

    /// <summary>Every well-known mask id, derived from the declarations above rather than restated.</summary>
    /// <remarks>
    /// <para>
    /// Reflection, deliberately. The alternative — a hand-maintained list — is what caused the bug this was
    /// added for: <c>RepositoryExporter.IsWellKnown</c> carried its own copy naming three of the eleven, and
    /// was never updated as the other eight arrived. Export then marked a Note, Contact or Appointment as NOT
    /// well-known, and import creates a fresh mask for anything not well-known — so the imported documents
    /// wore a DUPLICATE mask with a different id, and every <c>WellKnownMaskIds.Note</c> check (typed-folder
    /// containment, the IMAP projection, the clients' type column) stopped recognising them.
    /// </para>
    /// <para>
    /// A list derived from the fields cannot fall behind the fields. Adding a mask id above adds it here.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<Guid> All =
        typeof(WellKnownMaskIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Guid))
            .Select(f => (Guid)f.GetValue(null)!)
            .ToHashSet();
}

/// <summary>A typed folder and the masks it admits (#562 slice 5, set-valued for #564's notebook sections).</summary>
/// <param name="FolderMaskId">The mask the folder wears.</param>
/// <param name="FolderName">The folder mask's display name, for the invariant's message.</param>
/// <param name="Admits">Each mask a child may wear, with its display name for the message.</param>
public sealed record TypedFolderRule(Guid FolderMaskId, string FolderName, IReadOnlyList<(Guid MaskId, string Name)> Admits)
{
    /// <summary>The admitted masks, listed for a human — "Section or Note".</summary>
    public string AdmittedNames => string.Join(" or ", Admits.Select(a => a.Name));
}
