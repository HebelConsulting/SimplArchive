using SimplArchive.Domain.Masks;

namespace SimplArchive.Domain.CalDav;

/// <summary>The data half of a DAV collection kind: which masks, which extension, which UID field.</summary>
/// <param name="FolderMaskId">The well-known mask a folder wears to be this kind of collection.</param>
/// <param name="ItemMaskId">The mask its items wear.</param>
/// <param name="Extension">The item file extension (<c>.ics</c> / <c>.vcf</c>).</param>
/// <param name="UidFieldName">The item mask's UID field — resource names derive from it, falling back to the
/// document id.</param>
public sealed record DavCollectionKind(Guid FolderMaskId, Guid ItemMaskId, string Extension, string UidFieldName);

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
        new(WellKnownMaskIds.Calendar, WellKnownMaskIds.Appointment, ".ics", "Event UID");

    public static readonly DavCollectionKind Addressbook =
        new(WellKnownMaskIds.Addressbook, WellKnownMaskIds.Contact, ".vcf", "Contact UID");

    public static readonly IReadOnlyList<DavCollectionKind> All = [Calendar, Addressbook];

    /// <summary>The kind for a folder mask, or null when the folder is not a synced collection.</summary>
    public static DavCollectionKind? ForFolderMask(Guid? folderMaskId) =>
        All.FirstOrDefault(k => k.FolderMaskId == folderMaskId);
}
