using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

/// <summary>
/// A folder mask this mask may live directly inside. No rows at all means <b>anywhere</b>.
/// </summary>
/// <remarks>
/// <para>
/// The CHILD side of containment: "a Contact belongs in an Addressbook". Absence means no restriction, so the
/// table is a restriction rather than an obligation to enumerate — `Basic Entry` and plain `Folder` have no
/// rows and are welcome everywhere, which is what they were before any of this was modelled.
/// </para>
/// <para>
/// A collection, never a single reference: a `Note` lives in a Notebook <b>and</b> a Section, and a Section
/// lives in a Notebook <b>and inside itself</b>. The relation is neither one-to-one nor acyclic — the rules
/// were a list of pairs until sections arrived and broke exactly that shape.
/// </para>
/// </remarks>
public class MaskAllowedParent : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The mask being constrained — the child.</summary>
    public Guid MaskId { get; set; }

    /// <summary>A folder mask it may live directly inside.</summary>
    public Guid ParentMaskId { get; set; }
}

/// <summary>
/// A mask this folder mask admits as a child. Only consulted when the folder is exclusive.
/// </summary>
/// <remarks>
/// <para>
/// The PARENT side, and it is a separate table for the reason the static rules were split: a two-directional
/// table cannot express "a Mailbox <b>also</b> takes ordinary folders" without confining every folder in the
/// archive to a mailbox, nor "an IMAP Special folder holds <b>only</b> mail" without confining every mail to an
/// ephemeral folder. Both were real warnings written into the code they came from.
/// </para>
/// <para>
/// <b>Separating the directions is what removes the need for a mode.</b> A Mailbox simply declares
/// <c>{IMAP Special, Notebook, Folder}</c> — and because <c>Folder</c> has no <see cref="MaskAllowedParent"/>
/// rows, declaring it here widens the Mailbox without narrowing Folder. The "also" distinction the static
/// tables encode by existing separately is a consequence of this table's one-directionality, not a column it
/// needs.
/// </para>
/// <para>
/// What it still cannot say is <b>"no folders"</b> — see <c>Mask.AdmitsNoSubfolders</c>. An enumeration cannot
/// express an open-ended set, and declaring <c>{eMail}</c> on an ephemeral folder is the stricter, different
/// claim "only mail", which happens to coincide today only because mail is the one thing delivered there.
/// </para>
/// </remarks>
public class MaskAdmittedChild : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The folder mask doing the admitting.</summary>
    public Guid FolderMaskId { get; set; }

    /// <summary>A mask it admits as a direct child.</summary>
    public Guid ChildMaskId { get; set; }
}
