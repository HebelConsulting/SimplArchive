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

    /// <summary>
    /// Whether this folder's entries can be READ through the appointments surface — any <c>.ics</c> kind,
    /// read-only ones included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="AdmitsCalendarEntryCreation"/> on purpose, and this is the whole point of
    /// #1242.</b> One rel serves both methods on one address (ADR 0719): <c>GET</c> lists, <c>POST</c> creates.
    /// Deriving the REL from the create question makes a read-only collection unreadable — a module's Logbook
    /// listed in the Calendar tab, tickable, and permanently empty, because the tab correctly treats the absent
    /// rel as "not available here" (ADR 0543).
    /// </para>
    /// <para>
    /// Both answers come from THIS registry so that advertising and accepting cannot drift: they were two
    /// derivations of one question — a containment predicate over a core-only static table on the advertising
    /// side, the kind table on the accepting side — and drift is exactly what happened.
    /// </para>
    /// </remarks>
    bool ServesCalendarEntries(Guid? folderMaskId);

    /// <summary>
    /// Whether entries may be CREATED in this folder from the app — an <c>.ics</c> kind that is not read-only.
    /// </summary>
    /// <remarks>
    /// A module's read-only collection (ADR 0791) is append-only history the module writes; the app never
    /// creates in it. That is a property of the KIND, not of the caller's rights, so it belongs here rather
    /// than being re-derived beside each rights check.
    /// </remarks>
    bool AdmitsCalendarEntryCreation(Guid? folderMaskId);
}
