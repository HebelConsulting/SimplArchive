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

    /// <summary>What to draw for this entry — the mask's icon token, or null for the shape default.</summary>
    /// <remarks>
    /// So a menu entry wears the same glyph the thing will wear once it exists. Without it the menu says
    /// "Calendar" beside a generic folder and the tree then draws a calendar, which reads as two different
    /// actions.
    /// </remarks>
    public string? Icon { get; set; }

    /// <summary>
    /// What to ask the user for: <c>name</c>, <c>note</c> (a title and a body), <c>contact</c> or
    /// <c>appointment</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server names the INPUT because the client cannot name the mask. The desktop client deliberately does
    /// not reference the Domain project — it is an Api client, not a second copy of the model — so it has no
    /// <c>WellKnownMaskIds</c> to switch on, and inferring "this one needs a body" from whether it happens to be
    /// a folder would be reading a decision out of an incidental field.
    /// </para>
    /// <para>
    /// A CLOSED VOCABULARY of four values, each naming a dialog both clients already have (#689, owner-decided).
    /// It is deliberately not a form specification: describing the fields to collect, so any mask gets a create
    /// form, is a different and much larger piece of work, and one to enter on purpose rather than to arrive at
    /// by widening this field one mask at a time. An unknown value must fall back to the name prompt, so a
    /// client older than a new kind stays usable rather than offering an entry that does nothing.
    /// </para>
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
/// <b>Creatability is DATA now (#678).</b> Whether a user may make one of these at all is
/// <c>Mask.UserCreatable</c>, defaulting to true, with the six masks provisioning owns set false by the
/// seeder. What remains in code below is much smaller and is genuinely application knowledge: which masks have
/// a create endpoint of their OWN. Everything else creatable goes through the children collection carrying its
/// mask id, so a tenant-authored folder mask reaches a menu with no change here.
/// </para>
/// </remarks>
public static class CreatableChildren
{
    // Masks with a DEDICATED create endpoint, and what that endpoint asks the user for. Not a permission list
    // — permission is Mask.UserCreatable — just the four families whose create is not "make a folder".
    //
    // Contact and Appointment were absent until #689, and NOT because of creatability: they passed both the
    // other questions all along. What they lacked was a way to ASK. Their endpoints take a whole person or a
    // whole event, so a name prompt would have produced an empty vCard or a dateless appointment — worse than
    // no affordance. They arrive now with prompts of their own, naming the dialog each client already has for
    // the Contacts and Calendar tabs, so there is exactly one form per object rather than two that can come to
    // disagree about required fields.
    private static readonly Dictionary<Guid, (string Path, string Prompt)> DedicatedEndpoints = new()
    {
        [WellKnownMaskIds.NotebookSection] = ("sections", "name"),
        [WellKnownMaskIds.Note] = ("notes", "note"),
        [WellKnownMaskIds.Contact] = ("contacts", "contact"),
        [WellKnownMaskIds.Appointment] = ("appointments", "appointment"),
    };

    // The legacy folderMask slugs, emitted ALONGSIDE the mask id purely so a client built before #678 keeps
    // working. The endpoint accepts either; a tenant-authored mask has no slug and needs none.
    // Only the kinds whose entry goes through the CHILDREN collection are here. A family with its own endpoint
    // needs no body value at all — its address already says what it makes — and sending one there would be
    // noise a reader has to work out is unused.
    private static readonly Dictionary<Guid, string> LegacySlugs = new()
    {
        [WellKnownMaskIds.Folder] = "folder",
        [WellKnownMaskIds.Addressbook] = "addressbook",
        [WellKnownMaskIds.Calendar] = "calendar",
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
        // A personal space's first level holds only what provisioning put there (#634). A separate invariant
        // from containment, so it is answered separately — and answered FIRST, because nothing below it can
        // make an entry legal here.
        if (isPersonalRoot)
        {
            return [];
        }

        var admits = new List<CreatableChild>();

        // The plain folder first: it is what "New subfolder" has always meant, and a menu that reorders itself
        // per folder is one the user has to read rather than aim at.
        if (Offers(rules, WellKnownMaskIds.Folder, folderMaskId))
        {
            admits.Add(Entry(rules, documentId, WellKnownMaskIds.Folder));
        }

        // Everything else this folder will take, by NAME so the order is stable — a menu whose entries move
        // between renders is one the user has to read rather than aim at, and row order out of a database is
        // not defined.
        admits.AddRange(rules.UserCreatableMasks
            .Where(m => m != WellKnownMaskIds.Folder && Offers(rules, m, folderMaskId))
            .OrderBy(rules.NameOf, StringComparer.Ordinal)
            .Select(m => Entry(rules, documentId, m)));

        return admits;
    }

    /// <summary>Whether this folder offers to make one of <paramref name="maskId"/>.</summary>
    /// <remarks>
    /// Three independent questions, and all three must hold. <b>May anyone make one</b> — data on the mask.
    /// <b>Would it be allowed here</b> — containment, the same object the invariant uses, so a menu can never
    /// offer a create its own <c>SaveChanges</c> would refuse. And <b>is there a way to make one</b>: a folder
    /// mask is made through the children collection, and a couple of families have endpoints of their own.
    /// <para>
    /// The third is what keeps an ordinary folder's menu short. Containment PERMITS a Basic Entry or an eMail
    /// in an ordinary folder and both are user-creatable, but neither is something you make — you upload a
    /// file and get one. A menu built without this question would offer "New Basic Entry" beside "New folder",
    /// which is the outcome ADR 0656 rejected.
    /// </para>
    /// </remarks>
    private static bool Offers(MaskContainmentRules rules, Guid maskId, Guid? folderMaskId) =>
        rules.IsUserCreatable(maskId)
        && (rules.IsFolderMask(maskId) || DedicatedEndpoints.ContainsKey(maskId))
        && rules.Allows(maskId, folderMaskId);

    private static CreatableChild Entry(MaskContainmentRules rules, Guid documentId, Guid maskId)
    {
        // A family with its own endpoint goes there; everything else is the children collection's create,
        // carrying the mask id. That is the whole reason a tenant-authored mask needs no entry in any table:
        // its address is the one every folder already advertises.
        var (path, prompt) = DedicatedEndpoints.TryGetValue(maskId, out var dedicated)
            ? dedicated
            : ("children", "name");

        return new CreatableChild
        {
            MaskId = maskId,
            Name = rules.NameOf(maskId),
            Folder = rules.IsFolderMask(maskId),
            Href = $"/api/documents/{documentId}/{path}",
            Method = "POST",
            FolderMask = LegacySlugs.GetValueOrDefault(maskId),
            Prompt = prompt,
            Icon = rules.IconOf(maskId),
        };
    }
}
