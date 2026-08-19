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
}
