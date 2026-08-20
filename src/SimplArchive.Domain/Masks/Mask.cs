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
}
