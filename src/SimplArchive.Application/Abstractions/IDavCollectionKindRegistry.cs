using SimplArchive.Domain.CalDav;

namespace SimplArchive.Application.Abstractions;

/// <summary>
/// The DAV collection kinds the server serves — the CORE kinds plus any declared by loaded modules (ABI 0.24,
/// ADR 0791). Replaces the direct reads of the static <see cref="DavCollectionKinds"/> list so a module's
/// calendar-shaped folder (a flight-log Logbook) is a CalDAV collection like the core's own calendars.
/// </summary>
/// <remarks>
/// A singleton: module kinds are declared by the loaded assembly, not per tenant, so the set is fixed for the
/// process lifetime. Per-tenant behaviour follows from whether a tenant has folders wearing the mask, which
/// exist only where the module is activated and seeded.
/// </remarks>
public interface IDavCollectionKindRegistry
{
    /// <summary>Every kind — core and module — for the collection listing and folder-mask lookups.</summary>
    IReadOnlyList<DavCollectionKind> All { get; }

    /// <summary>The kind a folder mask denotes, or null when the mask is not a collection.</summary>
    DavCollectionKind? ForFolderMask(Guid? folderMaskId);

    /// <summary>Folder mask ids whose collections carry items of <paramref name="extension"/> (.ics/.vcf).</summary>
    IReadOnlyList<Guid> FolderMaskIds(string extension);

    /// <summary>Item mask ids for collections of <paramref name="extension"/> (.ics/.vcf).</summary>
    IReadOnlyList<Guid> ItemMaskIds(string extension);
}
