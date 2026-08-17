namespace SimplArchive.Domain.Masks;

// Fixed, cross-tenant Mask.Id values — every tenant's "Basic Entry"/"Folder"/"eMail" mask shares the exact
// same Id, matching how a real DMS ships a fixed set of default document-type identifiers rather than
// each tenant getting its own randomly-generated ones. See ADR "Mask composite primary key for cross-tenant
// well-known IDs" and ADR "Mask creation endpoint".
public static class WellKnownMaskIds
{
    public static readonly Guid BasicEntry = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E30");

    public static readonly Guid Folder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E31");

    public static readonly Guid EMail = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E32");

    // The mask a per-user personal space wears (ADR 0590). A personal repository is not a plain folder: it is
    // somebody's, and the metadata that belongs on it — a telephone number, an address to reach them at — has
    // nowhere to live on the fieldless Folder mask.
    public static readonly Guid UserFolder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E35");

    // The Notes pair (#562 slice 5, ADR "IMAP endpoint: Notes"): Personal/Notes wears NoteFolder — a TYPED
    // folder that admits only Note-masked children (the same containment idea #564's Contact/Calendar folders
    // share) — and every note wears Note.
    public static readonly Guid NoteFolder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E36");

    public static readonly Guid Note = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E37");

    // The CalDAV/CardDAV pairs (#564, ADR 0619): a Contact Folder admits only Contact-masked children, a
    // Calendar Folder only Calendar-masked ones — the same containment as the Notes pair above, and unlike
    // Notes these folders may sit ANYWHERE in the archive tree, each subscribable where the ACL allows.
    public static readonly Guid ContactFolder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E38");

    public static readonly Guid CalendarFolder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E39");

    public static readonly Guid Contact = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3A");

    public static readonly Guid Calendar = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3B");

    /// <summary>
    /// The typed-folder pairs, as data: a folder mask admits ONLY children wearing its item mask, and an item
    /// mask's primary location is ONLY such a folder (references may point anywhere). One table rather than a
    /// copy of the rule per pair — <see cref="SimplArchive.Domain.Masks.TypedFolderPair"/> names both sides so
    /// the invariant's message can say which type it is talking about.
    /// </summary>
    public static readonly IReadOnlyList<TypedFolderPair> TypedFolderPairs =
    [
        new(NoteFolder, Note, "Note Folder", "Note"),
        new(ContactFolder, Contact, "Contact Folder", "Contact"),
        new(CalendarFolder, Calendar, "Calendar Folder", "Calendar"),
    ];
}

/// <summary>A typed folder and the one item mask it admits (#562 slice 5, generalized for #564).</summary>
/// <param name="FolderMaskId">The mask the folder wears.</param>
/// <param name="ItemMaskId">The only mask its children may wear.</param>
/// <param name="FolderName">The folder mask's display name, for the invariant's message.</param>
/// <param name="ItemName">The item mask's display name, for the invariant's message.</param>
public sealed record TypedFolderPair(Guid FolderMaskId, Guid ItemMaskId, string FolderName, string ItemName);
