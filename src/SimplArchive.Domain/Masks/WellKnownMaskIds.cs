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

    /// <summary>The mask a user's MAILBOX wears — the node their delivered mail lives under (ADR 0628, #596).</summary>
    /// <remarks>
    /// <para>
    /// Fieldless. ADR 0627 designed this as an <c>IMAP Account</c> carrying host, username and an encrypted
    /// password, because we were then an IMAP client fetching from somebody's provider. ADR 0628 made us the
    /// DESTINATION — mail is delivered by an MTA over LMTP — so there is no account of anyone's to log into and
    /// nothing to configure: the address is derived (domain identifies the tenant, local part the user) rather
    /// than stored. The name follows the model rather than the history, since a node called "IMAP Account" with
    /// no credentials on it invites exactly the question ADR 0628 removed.
    /// </para>
    /// <para>
    /// A personal space admits at most ONE (see <see cref="ChildCardinalityRules"/>). That is a capacity rule on
    /// the FOLDER, not a placement rule on the mailbox: a personal space still holds ordinary documents, and the
    /// constraint is only that it never holds two mailboxes.
    /// </para>
    /// </remarks>
    public static readonly Guid Mailbox = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3E");

    /// <summary>
    /// A mailbox's standing IMAP folder — `INBOX` today, `SENT`/`DRAFTS`/`JUNK` when they arrive (#596).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mask is what makes these folders <b>ephemeral</b>: their content is a staging area, stored under the
    /// `mail/` key prefix rather than as members of the repository, and swept accordingly. That is why an
    /// `IMAP Folder` — which IS archive — may never live inside one: an archive folder beneath an ephemeral
    /// parent leaves the archive holding folders whose parent is not in the archive, and nothing downstream
    /// could detect it.
    /// </para>
    /// <para>
    /// Fieldless: it types the folder, and everything worth indexing lives on the messages inside it. Slot 3F
    /// rather than the free 33/34 — those are retired ids, and a returning id is worse than a gap.
    /// </para>
    /// </remarks>
    public static readonly Guid ImapSpecial = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E3F");

    /// <summary>
    /// A user-created mail folder inside the ephemeral tier — the mask behind "bring some order to the
    /// mailbox" (#802). Fieldless, like the Section it is shaped after: it types the folder, and the fields
    /// live on the mail.
    /// </summary>
    /// <remarks>
    /// Deliberately its OWN mask rather than a sixth use of <see cref="ImapSpecial"/>: that mask means
    /// "provisioned staging folder" and carries the standing five's invariants (nowhere but under a Mailbox,
    /// never user-created). What the two share is the TIER — <c>EphemeralMailFolder</c> counts both as
    /// staging, so mail inside a user folder keeps mail semantics (delete → Trash, no repository membership).
    /// Where it may live is <see cref="ConstrainedPlacements"/>: under a staging folder or under itself —
    /// loose by decision, so a mail folder under Inbox is legal even though IMAP only advertises creation
    /// under the archive.
    /// </remarks>
    public static readonly Guid ImapFolder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E41");

    /// <summary>The mask <c>My Documents</c> wears — a personal space's one general-purpose folder (#634).</summary>
    /// <remarks>
    /// <para>
    /// Fieldless, and identical in shape to <see cref="Folder"/>. It exists so that admission at the personal
    /// space's first level can be decided by MASK: that level holds only what it was provisioned with, and
    /// <c>My Documents</c> wearing a plain <see cref="Folder"/> would have made "no Folder here" refuse the very
    /// folder we provision.
    /// </para>
    /// <para>
    /// The same rule the codebase learned the hard way and states in ADR 0633 — <b>admission by mask,
    /// protection by name</b>. Deciding this one by name instead would put a name back into admission, which is
    /// exactly what refused a folder caught mid-rename.
    /// </para>
    /// </remarks>
    public static readonly Guid MyDocuments = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E40");

    /// <summary>A bookable meeting room (ADR 0735) — the core's own thin proof of the booking primitive.</summary>
    /// <remarks>
    /// Deliberately thin (ADR 0743's guard-rail): a room is a document with a location and a capacity, and
    /// stays a demonstration of the primitive, not the seed of facilities management. Bookability rides the
    /// mask (<see cref="Mask.IsBookable"/>), so it survives everything a mask survives.
    /// </remarks>
    public static readonly Guid MeetingRoom = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E42");

    /// <summary>A meeting-room reservation — the <c>.ics</c> in the room's Schedule (ADR 0744).</summary>
    /// <remarks>
    /// The booking IS the calendar entry: one document carrying the appointment facts (Event UID, Start,
    /// End, Location) and the domain payload (Purpose). The authoritative slot lives on the
    /// <c>ResourceBooking</c> row; the fields here are its lockstep projection.
    /// </remarks>
    public static readonly Guid RoomBooking = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E43");

    /// <summary>A meeting room's booking calendar (ADR 0744) — a calendar KIND with its own containment.</summary>
    /// <remarks>
    /// Its own mask rather than a plain <see cref="Calendar"/> so that every rule about it stays
    /// non-contextual: a Schedule exists only in a meeting room, holds only Room bookings, and serves them
    /// over CalDAV through its own <c>DavCollectionKinds</c> row — while ordinary calendars everywhere keep
    /// admitting ordinary appointments. A plain Appointment in a Schedule would be visible time the booking
    /// conflict check cannot see, which is why the admission is exclusive in both directions.
    /// </remarks>
    public static readonly Guid Schedule = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E45");

    /// <summary>A filed module-license artefact (ADRs 0740/0743) — the signed JSON a vendor issues.</summary>
    /// <remarks>
    /// A CORE mask, deliberately not module-seeded: it must exist before any module is activated, since
    /// activation is the act of referencing a document wearing it. Its fields (Module, Valid until) are a
    /// PROJECTION the server stamps from the VERIFIED claims after activation — the signed JSON inside the
    /// document stays the only truth, and an unverified license simply shows empty fields.
    /// </remarks>
    public static readonly Guid ModuleLicense = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E44");

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
        // The mailbox holds its standing IMAP folders and, at most, the one notebook (#596). Both directions
        // of this row matter: the mailbox admits nothing else, and — through AdmittingFolders — an
        // `IMAP Special` folder or a `Notebook` may exist NOWHERE ELSE in the archive. The second direction is
        // the one that was asked for: a Notebook needs Apple Notes and the IMAP projection to mean anything,
        // so loose in a repository it is a folder whose whole purpose is unreachable.
        new(Mailbox, "Mailbox", [(ImapSpecial, "IMAP Special"), (Notebook, "Notebook")]),
        new(Notebook, "Notebook", [(NotebookSection, "Section"), (Note, "Note")]),
        new(NotebookSection, "Section", [(NotebookSection, "Section"), (Note, "Note")]),
        new(Addressbook, "Addressbook", [(Contact, "Contact")]),
        new(Calendar, "Calendar", [(Appointment, "Appointment")]),
        // A meeting room holds exactly its Schedule, and the Schedule holds exactly its bookings
        // (ADR 0744). Both rows are deliberately two-directional: a Schedule outside a room would be a
        // booking calendar on nothing, a booking outside a Schedule a claim without a subject, and a plain
        // Appointment inside a Schedule would be visible time the conflict check cannot see. Rights still
        // flow from the room the normal way — see the room, see its schedule, see its bookings.
        new(MeetingRoom, "Meeting room", [(Schedule, "Schedule")]),
        new(Schedule, "Schedule", [(RoomBooking, "Room booking")]),
    ];

    /// <summary>How MANY children wearing a given mask a folder admits — a capacity rule, not an admission one.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately its own table rather than a column on <see cref="TypedFolderRules"/>, because it answers a
    /// different question about a different kind of folder. Admission asks "may this child be here at all?" and
    /// applies to folders that admit ONLY their listed masks; capacity asks "is there already one?" and applies
    /// to a folder that otherwise admits anything. A personal space is the latter: it holds whatever documents
    /// its owner puts there, and the single restriction is that it never holds two mailboxes.
    /// </para>
    /// <para>
    /// Folding this into <c>TypedFolderRules</c> would have required a flag saying "this rule constrains only
    /// half of what the others constrain", which is a table describing two rules while pretending to be one.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<ChildCardinalityRule> ChildCardinalityRules =
    [
        new(UserFolder, "Personal space", Mailbox, "Mailbox", 1),

        // One mailbox per plain folder (#703 PR 4): a DEPARTMENT mailbox lives in a named ordinary folder
        // (`Sales/Mailbox`), and two mailboxes in one folder is not a placement error but one too many — the
        // same shape as the personal-space rule above, extended rather than forked.
        new(Folder, "Folder", Mailbox, "Mailbox", 1),

        // One notebook per mailbox (#596). Admission above already says a Notebook lives only under a Mailbox;
        // this says how many, and the two are separate questions for the same reason the mailbox rule is: a
        // second notebook is not a placement error, it is one too many. IMAP projects it as `NOTES`, and a
        // client that discovers two of them has no way to choose.
        new(Mailbox, "Mailbox", Notebook, "Notebook", 1),

        // One Schedule per room (ADR 0744) — the booking flow files into THE schedule, so "which one?"
        // must have exactly one answer. This replaces the "oldest calendar wins" ordering the flow used
        // while the schedule was a plain Calendar, whose cardinality the decided boundary left uncapped.
        new(MeetingRoom, "Meeting room", Schedule, "Schedule", 1),

    ];

    /// <summary>
    /// The well-known masks a FOLDER wears, as opposed to an item that lives in one (#596).
    /// </summary>
    /// <remarks>
    /// Needed because <see cref="NoSubfolderMasks"/> asks a question no other table asks — "is this child a
    /// folder at all?" — and folder-ness is not otherwise a property of a mask. The alternative, deriving it
    /// from whether the document has versions, is not available where the rule runs: a folder and a freshly
    /// delivered message are both version-less at the instant <c>SaveChanges</c> validates them.
    /// <para>
    /// Hand-written, and therefore guarded: <c>WellKnownMaskPartitionTests</c> asserts every mask in
    /// <see cref="All"/> is classified here or in <see cref="ItemMasks"/> and never both, so adding a mask
    /// without saying which it is fails the build rather than silently landing on the item side — where it
    /// would be admitted into an ephemeral folder by default, which is the one outcome this exists to prevent.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<Guid> FolderMasks =
        new HashSet<Guid> { Folder, Repository, UserFolder, MyDocuments, Mailbox, ImapSpecial, ImapFolder, Notebook, NotebookSection, Addressbook, Calendar, MeetingRoom, Schedule };

    /// <summary>
    /// The file extensions that make a well-known mask the automatic choice for an upload (#671).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping already existed, scattered across <c>CalendarContactClassifier.Handles</c> and
    /// <c>DocumentFinalizer</c>, where the endpoint that lists masks could not see it. Stated here so the
    /// seeder can put it in the DATABASE, which is what lets a picker and a classifier reach the same answer.
    /// </para>
    /// <para>
    /// <b>Note is deliberately absent.</b> A note is stored as <c>.eml</c> — the same extension as a mail — and
    /// the two are told apart by WHERE they are filed, not by their bytes. So <c>.eml</c> belongs to
    /// <see cref="EMail"/> and a note gets its mask from the composer that writes it. Listing both would make
    /// the unique index on (tenant, extension) unsatisfiable, which is the constraint doing its job.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<Guid, IReadOnlyList<string>> FileExtensions =
        new Dictionary<Guid, IReadOnlyList<string>>
        {
            [EMail] = [".eml", ".msg"],
            [Contact] = [".vcf"],
            [Appointment] = [".ics"],
        };

    /// <summary>
    /// What each well-known mask is DRAWN as — a semantic token both clients map to their own icon set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vocabulary is the wire contract, exactly like the <c>folderMask</c> slugs: the server names the
    /// thing, and each client decides which glyph its set has for it. Web draws from Material, desktop from
    /// Material Design Icons, and no single icon NAME exists in both — so a name here could only ever be right
    /// for one of them.
    /// </para>
    /// <para>
    /// <b>Absent is meaningful.</b> <see cref="Folder"/>, <see cref="MyDocuments"/> and
    /// <see cref="BasicEntry"/> are deliberately not here: they ARE the generic folder and the generic
    /// document, so the shape default is already the right answer and a token would only give them a second
    /// way to say it. <see cref="Repository"/> is here despite also being a folder, because a repository root
    /// is a different KIND of thing from a folder inside one — it is where a tree starts.
    /// </para>
    /// <para>
    /// Seeded onto <see cref="Mask.Icon"/> per tenant and healed there, so a tenant-authored mask can carry a
    /// token from the same vocabulary. This table describes only the masks the application ships.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<Guid, string> IconTokens =
        new Dictionary<Guid, string>
        {
            [Repository] = "repository",
            [UserFolder] = "person",
            [Mailbox] = "mailbox",
            // The INBOX and its future siblings. "mail-folder" rather than "inbox" because SENT/DRAFTS/JUNK
            // wear the same mask and are not inboxes — naming the token for today's only instance would be
            // wrong the moment the second one arrives.
            [ImapSpecial] = "mail-folder",
            // A USER's mail folder, distinct from the standing tray: the two sit side by side under
            // My Mailbox, and two masks drawn identically are two things the eye cannot separate.
            [ImapFolder] = "mail-user-folder",
            [Notebook] = "notebook",
            [NotebookSection] = "section",
            [Addressbook] = "addressbook",
            [Calendar] = "calendar",
            // Its own token, not "calendar": a booking calendar and a personal one behave differently on
            // every surface that admits something, and two masks drawn identically are two things the eye
            // cannot separate (the ImapSpecial/ImapFolder rule, pinned by MaskIconVocabularyTests).
            [Schedule] = "schedule",
            [EMail] = "email",
            [Note] = "note",
            [Contact] = "contact",
            [Appointment] = "appointment",
        };

    /// <summary>
    /// The well-known masks a USER may not create directly — everything provisioning or a protocol owns (#678).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated as the EXCEPTIONS rather than as the permitted set, because <see cref="Mask.UserCreatable"/>
    /// defaults to true: a tenant who authors a mask should be able to use it, and a permitted-set table could
    /// only ever list the masks the application ships. Everything absent here is creatable.
    /// </para>
    /// <para>
    /// Each entry is a different reason, and the fact that each needed its own sentence in the Api is exactly
    /// why this became data:
    /// <c>Repository</c>, <c>User Folder</c> and <c>My Documents</c> are made by provisioning;
    /// <c>Mailbox</c> and <c>IMAP Special</c> by the mail path;
    /// and <c>Notebook</c> by the IMAP client, automatically — it IS declared by a Mailbox, so without this
    /// line an honest menu would offer "New Notebook" on every mailbox (owner-stated 2026-08-20).
    /// </para>
    /// <para>
    /// <c>Contact</c> and <c>Appointment</c> are deliberately ABSENT — they are user-creatable, from the
    /// Contacts and Calendar tabs. Their tree-menu entries need their own dialogs rather than a name prompt,
    /// which is its own piece of work; being creatable is not the thing standing in the way.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<Guid> NotUserCreatable =
        // Mailbox LEFT this set with #703 PR 4: a department mailbox is created by a person, in a plain
        // folder — placement and capacity say where and how many, creatability no longer says never.
        // Schedule joined with ADR 0744: the booking flow creates it, one per room, when the first booking
        // is filed — a hand-made second one would break the cardinality that makes "the schedule" singular.
        // RoomBooking stays here for the PLAIN create paths only — any .ics WRITE into a Schedule is the
        // real creation path and is gated by rights on the Schedule, not by this set.
        new HashSet<Guid> { Repository, UserFolder, MyDocuments, ImapSpecial, Notebook, RoomBooking, Schedule };

    /// <summary>The well-known masks an ITEM wears — the complement of <see cref="FolderMasks"/>.</summary>
    /// <remarks>Stated rather than derived, so the partition guard has two sides to compare instead of one.</remarks>
    public static readonly IReadOnlySet<Guid> ItemMasks =
        new HashSet<Guid> { BasicEntry, EMail, Note, Contact, Appointment, RoomBooking, ModuleLicense };

    /// <summary>
    /// Typed folders that ALSO admit a plain <see cref="Folder"/>, so a user can make folders of their own
    /// inside one (#596).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own one-directional table, and the reason is the same trap <see cref="NoSubfolderMasks"/> documents.
    /// <see cref="TypedFolderRules"/> is <b>two-directional</b>: a mask listed there may live ONLY in a folder
    /// admitting it. Adding <c>Folder</c> to the mailbox's row therefore did not mean "a mailbox may also hold
    /// folders" — it meant <b>every plain folder in the archive may live only inside a mailbox</b>, which took
    /// out ten integration tests at once and would have been a catastrophe in the wild.
    /// </para>
    /// <para>
    /// So this constrains the PARENT only: a mailbox may hold ordinary folders, and an ordinary folder is still
    /// welcome anywhere. Deliberately not a mask of its own either — a folder inside a mailbox is an archive
    /// folder that happens to live there (same retention, same recycle bin), and a mask adding no field and no
    /// rule costs an id and earns nothing.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(Guid FolderMaskId, string FolderName)> AlsoAdmitPlainFolders =
        [(Mailbox, "Mailbox")];

    /// <summary>Folder masks that admit no subfolders at all — only items (#596).</summary>
    /// <remarks>
    /// <para>
    /// An <c>IMAP Special</c> folder is <b>ephemeral</b>: its content is a staging area under the mail key
    /// prefix, not a member of the repository. An archive folder beneath it would therefore be an archive
    /// folder whose parent is not in the archive — a shape nothing else in the model can produce and no
    /// invariant downstream could detect.
    /// </para>
    /// <para>
    /// Expressed as "no subfolders" rather than as a <see cref="TypedFolderRules"/> row admitting only
    /// <see cref="EMail"/>, because that table is <b>two-directional</b>: admitting eMail there would also
    /// confine every eMail in the archive to an ephemeral folder, which is the opposite of what filing means.
    /// This rule constrains the parent only.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(Guid FolderMaskId, string FolderName)> NoSubfolderMasks =
        [(ImapSpecial, "IMAP Special")];

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

    /// <summary>
    /// The containment rules above, projected into the four facts the MODEL stores (#673).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The static tables do not disappear when containment becomes data — they become the <b>seed</b> for the
    /// well-known masks, exactly as <see cref="FileExtensions"/> did in #674. Projected here rather than
    /// restated in the seeder, for the reason <see cref="AdmittingFolders"/> gives about itself: a second
    /// hand-maintained copy is how the two representations of one rule drift apart, and the drift is invisible
    /// until something is filed where it should not be.
    /// </para>
    /// <para>
    /// Note what does NOT appear here. <see cref="ChildCardinalityRules"/> stays static and stays a different
    /// question — admission asks "may this child be here at all?", capacity asks "is there already one?".
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<Guid> ExclusiveFolderMasks =
        TypedFolderRules.Select(r => r.FolderMaskId).ToHashSet();

    /// <summary>Folder mask → the masks it admits as direct children. Only meaningful when exclusive.</summary>
    /// <remarks>
    /// <see cref="AlsoAdmitPlainFolders"/> folds in here and needs no mode of its own: a plain
    /// <see cref="Folder"/> has no allowed parents, so listing it widens the mailbox without confining folders
    /// to mailboxes. The "also" was never a property of the row — it was a consequence of the two directions
    /// living in one table.
    /// </remarks>
    // The one-directional AlsoAdmit table (a MeetingRoom also admitting a plain Calendar) is GONE
    // (ADR 0744): the room's calendar is now the Schedule mask, admitted through the two-directional
    // table like every other typed child, and no second admission shape remains to need it.

    public static readonly IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> AdmittedChildMasks =
        TypedFolderRules
            .Select(rule => (
                rule.FolderMaskId,
                Admits: rule.Admits.Select(a => a.MaskId)
                    .Concat(AlsoAdmitPlainFolders.Any(m => m.FolderMaskId == rule.FolderMaskId) ? [Folder] : [])))
            .ToDictionary(x => x.FolderMaskId, x => (IReadOnlySet<Guid>)x.Admits.ToHashSet());

    /// <summary>Mask → the folder masks it may live directly inside. Absent means anywhere.</summary>
    /// <summary>
    /// Masks whose PRIMARY LOCATION is constrained without any folder declaring them (#703 PR 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Mailbox cannot ride <see cref="TypedFolderRules"/>: that table is two-directional, and a row
    /// "Folder admits Mailbox" would make the plain folder EXCLUSIVE — refusing every ordinary document
    /// everywhere. This is the one-directional half only: where a Mailbox may be, saying nothing about what
    /// else its parent may hold.
    /// </para>
    /// <para>
    /// <c>Folder</c> and <c>UserFolder</c>, deliberately short (owner-decided 2026-08-22, refined for roots):
    /// a repository root wears <c>Repository</c> (ADR 0627), so a mailbox cannot sit directly under a root —
    /// a department mailbox lives in a named plain folder, which is also where it reads naturally. Typed
    /// containers (Calendar, Notebook, …) never hold one, keeping mailbox-in-mailbox impossible by
    /// construction. <c>UserFolder</c> is the standing personal-space admission (#634), restated here because
    /// allowed-parents is now CONSTRAINED for this mask — omitting it would refuse the provisioner's own
    /// "My Mailbox".
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> ConstrainedPlacements =
        new Dictionary<Guid, IReadOnlySet<Guid>>
        {
            [Mailbox] = new HashSet<Guid> { Folder, UserFolder },

            // A mail folder lives in the staging tier and nowhere else — under a provisioned staging folder
            // or under another mail folder. This row is also what opens the staging folders' no-subfolder
            // gate for it: a leaf refusal yields to a child that DECLARES the leaf as its parent (#802).
            [ImapFolder] = new HashSet<Guid> { ImapSpecial, ImapFolder },
        };

    public static readonly IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> AllowedParentMasks =
        AdmittingFolders.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<Guid>)pair.Value.Select(r => r.FolderMaskId).ToHashSet())
            .Concat(ConstrainedPlacements)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>
    /// Folder masks whose type may not be changed once a folder wears one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The PRINCIPLE is what re-typing COSTS, not where the folder may live. Turn a Calendar into a plain
    /// folder and the only thing lost is subscribability through CalDAV — the appointments inside remain
    /// perfectly good documents in a perfectly good folder. Turn a Mailbox or a Notebook into one and you break
    /// what the content depends on: mail has nowhere to arrive, and a notebook's whole purpose is a projection
    /// that no longer exists. The first is a preference a user may change their mind about; the second destroys
    /// the meaning of what is already inside.
    /// </para>
    /// <para>
    /// This set DERIVES that from constraint — a folder mask the containment rules will not let live just
    /// anywhere, by admission or by capacity — because a hand-maintained list of four is exactly what this file
    /// has been bitten by before. But the derivation is a PROXY for the principle, not the principle itself:
    /// nothing guarantees a future location-constrained mask is also one whose re-typing breaks its content, or
    /// the reverse. <c>ImmutableStructuralMaskTests</c> therefore pins today's answer, so a divergence fails
    /// loudly and someone decides, rather than the rule silently widening or narrowing.
    /// </para>
    /// <para>
    /// Note the direction: the rule forbids changing AWAY from one of these, never having one. Provisioning and
    /// the personal-space heal assign them to maskless folders, and a restamp moves a folder off plain
    /// <see cref="Folder"/> — a rule reading "wears a structural mask ⇒ refuse" would break the very paths that
    /// create them, which is the shape #630 got wrong three times.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<Guid> ImmutableStructuralMasks =
        AdmittingFolders.Keys
            .Concat(ChildCardinalityRules.Select(r => r.ChildMaskId))
            .Where(FolderMasks.Contains)
            .ToHashSet();

    /// <summary>Folder masks that hold documents only — the fourth fact, one-directional.</summary>
    /// <remarks>
    /// A restatement of <see cref="NoSubfolderMasks"/> without the display name, which the model does not need:
    /// the invariant reads the folder's CURRENT mask version for its message, so a renamed mask produces the
    /// right message instead of the one hardcoded here.
    /// </remarks>
    public static readonly IReadOnlySet<Guid> LeafFolderMasks =
        NoSubfolderMasks.Select(m => m.FolderMaskId).ToHashSet();

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
    /// <summary>
    /// Index fields the CLASSIFIER owns on a collection-kind item — the lockstep projection of the stored
    /// bytes (ADRs 0743/0744), keyed by mask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are read-only on the metadata surface, in both clients AND at the PUT (one entrance is not a
    /// rule): a pane edit would change only the projection, so the .ics/.vcf every synced client renders
    /// would disagree with the pane until the next content write silently overwrote the edit — and for a
    /// booking the claimed slot would not move at all. The UIDs are the DAV correlation keys on top: change
    /// one and the next sync forks the item into a duplicate (the contact-UID lesson).
    /// </para>
    /// <para>
    /// Deliberately NOT everything the classifier writes: Location and Purpose are secondary, genuinely
    /// useful to edit in the pane, and their next-content-write overwrite is an acceptable trade
    /// (owner-decided 2026-09-04). The real write path for times is the appointment editor / a rebooking.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<Guid, IReadOnlySet<string>> ClassifierOwnedFields =
        new Dictionary<Guid, IReadOnlySet<string>>
        {
            [Appointment] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Event UID", "Start", "End" },
            [RoomBooking] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Event UID", "Start", "End" },
            [Contact] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Contact UID" },
        };

    /// <summary>The well-known masks whose documents are bookable resources (ADR 0735).</summary>
    /// <remarks>
    /// The seed for <see cref="Mask.IsBookable"/>, healed unconditionally like the icon and creatability
    /// facts — a shipped mask cannot drift from what this release says it is. A tenant-authored mask sets
    /// the column directly; this set only describes what the application ships.
    /// </remarks>
    public static readonly IReadOnlySet<Guid> BookableMasks =
        new HashSet<Guid> { MeetingRoom };

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

/// <summary>A cap on how many children wearing one mask a folder may hold (#596).</summary>
/// <param name="FolderMaskId">The mask the folder wears.</param>
/// <param name="FolderName">The folder mask's display name, for the invariant's message.</param>
/// <param name="ChildMaskId">The mask being counted.</param>
/// <param name="ChildName">The counted mask's display name, for the invariant's message.</param>
/// <param name="Max">The most a folder may hold. Soft-deleted children do not count — consistent with sibling
/// name uniqueness, and the reason the check belongs in <c>SaveChanges</c>: a RESTORE is a save too, so
/// restoring a mailbox alongside a replacement is refused at the same point rather than needing its own rule.</param>
public sealed record ChildCardinalityRule(
    Guid FolderMaskId, string FolderName, Guid ChildMaskId, string ChildName, int Max);

/// <summary>A typed folder and the masks it admits (#562 slice 5, set-valued for #564's notebook sections).</summary>
/// <param name="FolderMaskId">The mask the folder wears.</param>
/// <param name="FolderName">The folder mask's display name, for the invariant's message.</param>
/// <param name="Admits">Each mask a child may wear, with its display name for the message.</param>
public sealed record TypedFolderRule(Guid FolderMaskId, string FolderName, IReadOnlyList<(Guid MaskId, string Name)> Admits)
{
    /// <summary>The admitted masks, listed for a human — "Section or Note".</summary>
    public string AdmittedNames => string.Join(" or ", Admits.Select(a => a.Name));
}
