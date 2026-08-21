using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

// A stable identity that outlives edits — see ADR "Mask versioning data shape". Name and field
// definitions belong to a specific MaskVersion, not to this identity, since masks are immutable
// versions (ADR "Mask/schema versioning and migration strategy"): editing a mask creates a new
// MaskVersion rather than mutating an existing one.
public class Mask : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Whether this mask types a FOLDER — and therefore may never be chosen for a filed document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not called <c>IsFolder</c>: that name is already taken across the clients and the search
    /// projection for a different question — whether a DOCUMENT has no versions. This one is about the mask,
    /// and the two are not the same fact. A document is a folder because of what it contains; a mask is a
    /// folder mask because of what it means.
    /// </para>
    /// <para>
    /// On the identity rather than on <see cref="MaskVersion"/>, because it does not change when a version is
    /// cut. Versioning it would permit a v2 that contradicts v1 while documents wear both, and nothing could
    /// then answer "is this a folder mask" without asking which version.
    /// </para>
    /// <para>
    /// It exists as a column rather than being derived from <c>WellKnownMaskIds.FolderMasks</c> because that
    /// table can only ever describe the masks the application ships. This has to be true of a TENANT-authored
    /// mask too — which is what lets a new typed-folder family be a data change rather than a change in both
    /// clients (#671).
    /// </para>
    /// </remarks>
    public bool IsFolderMask { get; set; }

    /// <summary>The file extensions that make this mask the automatic choice. Empty for most masks.</summary>
    public ICollection<MaskFileExtension> FileExtensions { get; set; } = [];

    /// <summary>
    /// Whether this folder admits ONLY the children it declares — an Addressbook holds contacts and nothing
    /// else, while a plain Folder holds anything.
    /// </summary>
    /// <remarks>
    /// The exclusivity switch. Without it, <see cref="AdmittedChildren"/> could only ever widen, and a typed
    /// folder's whole point is that it narrows. False for an ordinary folder, so the default is the permissive
    /// behaviour every mask had before containment was modelled (#673).
    /// </remarks>
    public bool AdmitsOnlyDeclaredChildren { get; set; }

    /// <summary>Whether this folder holds documents only — no subfolders of any kind (#673).</summary>
    /// <remarks>
    /// <para>
    /// The fourth fact, and the one I first argued away. An <c>IMAP Special</c> folder is ephemeral staging
    /// under the <c>mail/</c> key prefix, so an archive folder beneath it would be an archive folder whose
    /// parent is not in the archive. Declaring <see cref="AdmittedChildren"/> = <c>{eMail}</c> reproduces
    /// today's TESTS but not today's RULE: it also refuses an ordinary document, which the rule permits.
    /// </para>
    /// <para>
    /// The structural reason it needs its own flag is the same one that makes empty
    /// <see cref="AllowedParents"/> mean <i>anywhere</i>: <b>an enumeration cannot express an open-ended
    /// set</b>. "Anything that is not a folder" includes every mask a tenant has not authored yet, so no list
    /// of admitted masks can state it — and a folder that holds only documents is a thing a tenant-authored
    /// mask should be able to say without enumerating the archive's future.
    /// </para>
    /// <para>
    /// One-directional, like the table it replaces: it constrains the PARENT only. Expressing it as an
    /// admission row would make it two-directional and confine every eMail in the archive to an ephemeral
    /// folder — the trap <c>NoSubfolderMasks</c> was split out to avoid, and which I walked back into.
    /// </para>
    /// </remarks>
    public bool AdmitsNoSubfolders { get; set; }

    /// <summary>What a client should DRAW for a document wearing this mask — a token, not a glyph name.</summary>
    /// <remarks>
    /// <para>
    /// A semantic token (<c>calendar</c>, <c>mailbox</c>) rather than an icon name, because the two clients draw
    /// from different icon sets: the web from Material, the desktop from Material Design Icons. A concrete name
    /// could serve only one of them, and the first mask whose glyph exists in one set and not the other would
    /// make the column a lie in whichever client lost.
    /// </para>
    /// <para>
    /// <b>Null means "use the shape default"</b> — the folder/document/shortcut glyphs a row has always had.
    /// So a mask with no token renders exactly as it did before this column existed, which is what makes the
    /// change additive for every tenant-authored mask and for the four shipped ones that keep the plain
    /// glyphs.
    /// </para>
    /// <para>
    /// Deliberately NOT constrained to a vocabulary in the database. An <b>unrecognised</b> token falls back to
    /// the same shape default, so a token can be added without a migration and an older client meeting a newer
    /// token degrades to the generic icon rather than failing. A CHECK constraint would buy validation at the
    /// price of a migration per token, and would turn a cosmetic unknown into a write error.
    /// </para>
    /// <para>
    /// On the identity rather than <see cref="MaskVersion"/>, for the reason <see cref="IsFolderMask"/> gives:
    /// it does not change when a version is cut, and a v2 whose icon contradicts v1 would leave "what is this
    /// drawn as" unanswerable while documents wear both.
    /// </para>
    /// </remarks>
    public string? Icon { get; set; }

    /// <summary>Whether a USER may create a document wearing this mask directly (#678).</summary>
    /// <remarks>
    /// <para>
    /// The fifth fact. Containment says WHERE a mask may live; this says whether a person may make one at all.
    /// Nothing in the other four distinguishes a <c>Notebook</c> — which the IMAP client creates automatically
    /// and no menu ever offers — from an <c>Addressbook</c>, which a user makes deliberately. Both are folder
    /// masks admitted somewhere, and before this the difference lived only as absence from a hardcoded table
    /// in the Api, where every omission needed its own explanatory sentence.
    /// </para>
    /// <para>
    /// <b>Defaults to true</b>, so a tenant who authors a mask can use it without a second step — which is the
    /// complaint this exists to answer. The masks that must never be offered are the ones provisioning owns,
    /// and the seeder sets those false explicitly.
    /// </para>
    /// <para>
    /// The default lives HERE, on the CLR property, rather than as an EF <c>HasDefaultValue(true)</c>, and the
    /// migration backfills existing rows. This is the shape ADR "EF store default" recommends — every insert
    /// sends an explicit value, so no value of the property becomes unreachable.
    /// <b>Measured, not assumed:</b> on EF Core 10 a <c>HasDefaultValue(true)</c> would ALSO have been safe
    /// here — it derives the sentinel from the configured default, so <c>false</c> is still sent. The older
    /// trap (sentinel = the CLR default, making that value unstorable) did not reproduce for a bool on this
    /// version. The initializer is preferred because it needs no such reasoning to be correct, not because
    /// the alternative was proven broken.
    /// </para>
    /// <para>
    /// Not the same question as <i>may THIS caller create one HERE</i>: that is rights
    /// (<c>CanCreateSubItems</c>) and containment, both asked separately. This is a property of the KIND of
    /// thing, not of a caller or a place.
    /// </para>
    /// </remarks>
    public bool UserCreatable { get; set; } = true;

    /// <summary>Folder masks this mask may live directly inside. Empty means anywhere.</summary>
    public ICollection<MaskAllowedParent> AllowedParents { get; set; } = [];

    /// <summary>Masks this folder admits as children. Consulted when <see cref="AdmitsOnlyDeclaredChildren"/>.</summary>
    public ICollection<MaskAdmittedChild> AdmittedChildren { get; set; } = [];
}
