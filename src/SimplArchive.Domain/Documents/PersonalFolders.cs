using SimplArchive.Domain.Masks;

namespace SimplArchive.Domain.Documents;

/// <summary>
/// The folders a personal space is provisioned with, and the rule about what else may sit beside them (#596).
/// </summary>
/// <remarks>
/// The names live in the Domain rather than beside the provisioner that creates them, because the invariant
/// that protects them is enforced in <c>SaveChanges</c> — Infrastructure, which cannot reference the Api layer.
/// Two places knowing the same four strings independently is exactly how one of them drifts.
/// </remarks>
public static class PersonalFolders
{
    public const string MyDocuments = "My Documents";

    public const string MyCalendar = "My Calendar";

    public const string MyAddressbook = "My Addressbook";

    public const string MyMailbox = "My Mailbox";

    /// <summary>
    /// The folders that cannot be deleted or moved out of the personal space.
    /// </summary>
    /// <remarks>
    /// Each is a fixed root something resolves against: a CalDAV client is subscribed to the calendar, a
    /// CardDAV client to the addressbook, mail is delivered into the mailbox, and <see cref="MyDocuments"/> is
    /// where a migration puts anything that may not sit at the first level. Provisioning them is worth nothing
    /// if the next click can remove them, and the failure would surface on somebody's phone rather than here.
    /// </remarks>
    public static readonly IReadOnlyList<string> Protected = [MyDocuments, MyCalendar, MyAddressbook, MyMailbox];

    public static bool IsProtected(string? name) =>
        name is not null && Protected.Contains(name, StringComparer.Ordinal);

    /// <summary>The masks that may sit at the FIRST LEVEL of a personal space (#596).</summary>
    /// <remarks>
    /// <para>
    /// Exactly the masks the PROVISIONED folders wear, and nothing else — the first level is closed, and a
    /// user adds folders inside <see cref="MyDocuments"/> rather than beside it (#634). The plain
    /// <see cref="WellKnownMaskIds.Folder"/> was admitted here until then, which is what let a user create
    /// folders at this level and, less obviously, what let an UPLOAD land here: a create stamps the Folder mask
    /// and the finalizer reclassifies it afterwards, so the file walked in as a folder.
    /// Their own uniqueness is the sibling-name rule and, for the mailbox, the cardinality cap — this set only
    /// says which KINDS belong there at all.
    /// </para>
    /// <para>
    /// Here rather than private to the DbContext because two things need the same answer and must not each
    /// keep their own copy: <c>SaveChanges</c> REFUSES what is not admitted, and the repository importer
    /// RE-PARENTS it instead of emitting it where it would be refused (#630). A second hand-maintained list
    /// would make the importer's fallback silently disagree with the rule it exists to satisfy.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// A <c>Guid[]</c> rather than a set, and that is load-bearing: this is used inside an EF query, and
    /// <c>IReadOnlySet&lt;T&gt;.Contains</c> does not translate — the provider throws at query time rather than
    /// at build time, so the cost of the tidier type is a runtime failure in whichever endpoint touches it.
    /// </remarks>
    public static readonly Guid[] FirstLevelMasks =
    [
        WellKnownMaskIds.MyDocuments,
        WellKnownMaskIds.Calendar,
        WellKnownMaskIds.Addressbook,
        WellKnownMaskIds.Mailbox,
    ];
}
