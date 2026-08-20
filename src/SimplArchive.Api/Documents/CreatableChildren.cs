using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Masks;

namespace SimplArchive.Api.Documents;

/// <summary>
/// One kind of child a folder will accept, with everything a client needs to create it (#673).
/// </summary>
/// <remarks>
/// A plain mutable class with a parameterless constructor because responses negotiate to XML as well as JSON
/// (ADR 0190) and <c>XmlSerializer</c> requires that shape — which is also why the body is a NAMED field
/// rather than a dictionary of arbitrary keys: a dictionary does not survive XML at all.
/// </remarks>
public class CreatableChild
{
    /// <summary>The mask a child created this way will wear.</summary>
    public Guid MaskId { get; set; }

    /// <summary>What to call it on a menu — the mask's name on its current version, so a rename follows.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether creating this makes a folder, so a client can group or icon it.</summary>
    public bool Folder { get; set; }

    /// <summary>Where to POST. Advertised, never composed — the client appends nothing to it (ADR 0543).</summary>
    public string Href { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    /// <summary>
    /// The <c>folderMask</c> value to send in the body, or null when the address alone says what to make.
    /// </summary>
    /// <remarks>
    /// Handed to the client rather than derived by it. That is the whole point: the client sends back what the
    /// server gave it, so the vocabulary stays the server's and a client never maps a mask to a slug.
    /// </remarks>
    public string? FolderMask { get; set; }

    /// <summary>What to ask the user for: <c>name</c>, or <c>note</c> for a title and a body.</summary>
    /// <remarks>
    /// The server names the INPUT because the client cannot name the mask. The desktop client deliberately does
    /// not reference the Domain project — it is an Api client, not a second copy of the model — so it has no
    /// <c>WellKnownMaskIds</c> to switch on, and inferring "this one needs a body" from whether it happens to be
    /// a folder would be reading a decision out of an incidental field.
    /// </remarks>
    public string Prompt { get; set; } = "name";
}

/// <summary>
/// Builds the <see cref="CreatableChild"/> list for a folder, from the tenant's containment rules.
/// </summary>
/// <remarks>
/// <para>
/// The list is what a folder <b>declares</b> it admits, not everything containment would permit. An ordinary
/// folder permits nearly every mask, and a menu built from permission would offer "New Basic Entry" beside
/// "New folder"; a declaration is an intent to hold something, and only that belongs on a menu. The plain
/// folder is added separately for the same reason it is a separate rule in the model — it is admitted
/// everywhere that does not refuse it, rather than declared anywhere.
/// </para>
/// <para>
/// <b>What is still hardcoded, and why it is the remaining gap.</b> The model says WHERE each mask may live;
/// it does not say whether a user may create one directly, nor at which address. <c>Repository</c>,
/// <c>User Folder</c>, <c>My Documents</c>, <c>Mailbox</c> and <c>IMAP Special</c> are all folder masks that
/// only provisioning creates, and nothing in the four facts distinguishes them from a Notebook. So the table
/// below is a fifth fact living in code — deliberately, for now: #673's scope was the well-known masks, and a
/// tenant-authored typed folder needs this to become data before it can appear on a menu at all.
/// </para>
/// </remarks>
public static class CreatableChildren
{
    // Mask → the address that creates one, relative to the folder, and the body value where the address is
    // shared. A mask absent here is one a USER does not create from a menu, which is why absence is the default.
    //
    // "Absent" is not the same as "the API refuses it": `CreatableFolderMasks` on the children endpoint accepts
    // more than this, because a protocol client legitimately asks for things a menu never offers. This table is
    // about the MENU.
    //
    // Deliberately absent, and each for a different reason:
    //   Notebook             — the IMAP client creates it automatically; it is never offered in the UI
    //                          (owner-stated 2026-08-20). It IS declared by a Mailbox, so without this line an
    //                          honest admits list would have put "New Notebook" on every mailbox.
    //   Mailbox, IMAP Special — provisioning and the mail path own them.
    //   Repository, User Folder, My Documents — provisioning only.
    //   Addressbook, Calendar — user-creatable, but from the Contacts and Calendar tabs, which is where someone
    //                          looking for a new one goes. Nothing declares them, so they would not appear here
    //                          anyway; named so the omission reads as a decision rather than an oversight.
    //   Contact, Appointment  — the same: made in the tab that shows them, where the dialog for a person or an
    //                          event belongs. Listing them here would put "New Contact" in the TREE's menu on
    //                          every addressbook, which is a new affordance rather than the same one sourced
    //                          differently — out of scope while this change is behaviour-preserving.
    //
    // The addresses match the rels the clients already follow (`sections`, `notes`), so a menu entry lands on
    // the endpoint that already exists rather than on a second way in. `folder` alone goes through `children`
    // with a body value, because a plain folder has no rel of its own — it IS the children collection's create.
    //
    // That every one of these needs a sentence is the argument for making it DATA: the model says where a mask
    // may live, and nothing in the four facts says who may create one. See the remarks above.
    private static readonly Dictionary<Guid, (string Path, string? FolderMask, string Prompt)> Creates = new()
    {
        [WellKnownMaskIds.Folder] = ("children", "folder", "name"),
        [WellKnownMaskIds.NotebookSection] = ("sections", null, "name"),
        [WellKnownMaskIds.Note] = ("notes", null, "note"),
    };

    /// <param name="rules">The tenant's containment, loaded once per request and shared with the invariant.</param>
    /// <param name="documentId">The folder the list is for — its id appears in every href.</param>
    /// <param name="folderMaskId">The mask that folder wears; null for one not yet classified.</param>
    /// <param name="isPersonalRoot">
    /// A personal space's first level is closed to all but its provisioned folders (#634) — a separate
    /// invariant from containment, so it is asked separately here too.
    /// </param>
    public static List<CreatableChild> For(
        MaskContainmentRules rules, Guid documentId, Guid? folderMaskId, bool isPersonalRoot)
    {
        var admits = new List<CreatableChild>();

        // The plain folder first: it is what "New subfolder" has always meant, and a menu that reorders itself
        // per folder is one the user has to read rather than aim at.
        if (!isPersonalRoot && rules.Allows(WellKnownMaskIds.Folder, folderMaskId))
        {
            admits.Add(Entry(rules, documentId, WellKnownMaskIds.Folder));
        }

        if (folderMaskId is not { } maskId)
        {
            return admits;
        }

        foreach (var declared in rules.AdmittedBy(maskId))
        {
            // Already added above, and a folder that declares it must not list it twice.
            if (declared == WellKnownMaskIds.Folder || !Creates.ContainsKey(declared))
            {
                continue;
            }

            admits.Add(Entry(rules, documentId, declared));
        }

        return admits;
    }

    private static CreatableChild Entry(MaskContainmentRules rules, Guid documentId, Guid maskId)
    {
        var (path, folderMask, prompt) = Creates[maskId];
        return new CreatableChild
        {
            MaskId = maskId,
            Name = rules.NameOf(maskId),
            Folder = rules.IsFolderMask(maskId),
            Href = $"/api/documents/{documentId}/{path}",
            Method = "POST",
            FolderMask = folderMask,
            Prompt = prompt,
        };
    }
}
