using SimplArchive.Domain.Masks;

namespace SimplArchive.Domain.CalDav;

/// <summary>The data half of a DAV collection kind: which masks, which extension, which UID field.</summary>
/// <param name="FolderMaskId">The well-known mask a folder wears to be this kind of collection.</param>
/// <param name="ItemMaskId">The mask its items wear.</param>
/// <param name="Extension">The item file extension (<c>.ics</c> / <c>.vcf</c>).</param>
/// <param name="UidFieldName">The item mask's UID field — resource names derive from it, falling back to the
/// document id.</param>
/// <param name="Name">
/// The kind's stable wire name, as <c>GET /api/dav-collections</c> reports it per collection (#1122).
/// </param>
/// <remarks>
/// <see cref="Name"/> is finer than the listing's <c>kind</c>, which answers only <c>addressbook</c> or
/// <c>calendar</c> — every .ics collection reports <c>calendar</c> there, so a client could not tell a
/// Schedule from an Availability and therefore could not offer a MOVE that would be accepted. It lives on the
/// kind itself rather than in a map beside the listing, because a map beside the listing is precisely the
/// hand-written mask list that left Maintenance and Availability unusable in the first place.
/// </remarks>
public sealed record DavCollectionKind(Guid FolderMaskId, Guid ItemMaskId, string Extension, string UidFieldName, string Name);

/// <summary>
/// The collection kinds, stated ONCE in the Domain (#806). The Api's protocol objects carry the wire half
/// (paths, namespaces, report names) and read these four facts from here; the change recorder in the
/// DbContext reads them too — it lives below the Api and must answer "is this folder a synced collection,
/// and what is this item's resource name" without a protocol object in scope. Two copies of that answer is
/// how a workbench write gets logged under one name and served under another.
/// </summary>
public static class DavCollectionKinds
{
    public static readonly DavCollectionKind Calendar =
        new(WellKnownMaskIds.Calendar, WellKnownMaskIds.Appointment, ".ics", "Event UID", "calendar");

    public static readonly DavCollectionKind Addressbook =
        new(WellKnownMaskIds.Addressbook, WellKnownMaskIds.Contact, ".vcf", "Contact UID", "addressbook");

    /// <summary>A meeting room's Schedule (ADR 0744): calendar wire behaviour, Room-booking items.</summary>
    public static readonly DavCollectionKind Schedule =
        new(WellKnownMaskIds.Schedule, WellKnownMaskIds.Booking, ".ics", "Event UID", "schedule");

    /// <summary>A resource's Maintenance collection (ADR 0778): calendar wire behaviour, block items.</summary>
    /// <remarks>
    /// Its own kind rather than a second use of <see cref="Schedule"/>, so that a client subscribing to an
    /// aircraft sees two collections and can tell a flight from a grounding. Both serve <c>.ics</c> and index
    /// the same UID field, which is what lets one classifier handle either.
    /// </remarks>
    public static readonly DavCollectionKind Maintenance =
        new(WellKnownMaskIds.Maintenance, WellKnownMaskIds.MaintenanceBlock, ".ics", "Event UID", "maintenance");

    /// <summary>A resource's Availability collection (ADR 0780): calendar wire behaviour, window items.</summary>
    /// <remarks>
    /// Served over CalDAV because publishing availability is something a person does from the calendar they
    /// already carry — an instructor marking Thursday afternoon free on their phone. A collection the core
    /// knows only internally could be filled from the workbench alone, which is not where this happens.
    /// </remarks>
    public static readonly DavCollectionKind Availability =
        new(WellKnownMaskIds.Availability, WellKnownMaskIds.AvailabilityWindow, ".ics", "Event UID", "availability");

    public static readonly IReadOnlyList<DavCollectionKind> All = [Calendar, Addressbook, Schedule, Maintenance, Availability];

    /// <summary>The kind for a folder mask, or null when the folder is not a synced collection.</summary>
    public static DavCollectionKind? ForFolderMask(Guid? folderMaskId) =>
        All.FirstOrDefault(k => k.FolderMaskId == folderMaskId);
}
