using SimplArchive.Domain.Masks;

namespace SimplArchive.Api.CalDav;

/// <summary>
/// Everything that differs between CalDAV and CardDAV (#564, ADR 0619). The two protocols are the same
/// machinery over a different item type, so the gateway is written once against this descriptor and each
/// protocol is one instance of it — rather than two near-identical middlewares that drift apart.
/// </summary>
/// <param name="BasePath">The protocol's URL root, e.g. <c>/caldav</c>.</param>
/// <param name="CollectionsSegment">The path segment holding the collections, e.g. <c>calendars</c>.</param>
/// <param name="Namespace">The protocol's XML namespace URI.</param>
/// <param name="NamespacePrefix">The prefix bound to it in emitted XML.</param>
/// <param name="HomeSetProperty">The home-set property name a client discovers on the principal.</param>
/// <param name="CollectionResourceType">The element marking a collection as this protocol's kind.</param>
/// <param name="FolderMaskId">The well-known mask a folder must wear to be one of this protocol's collections.</param>
/// <param name="ItemMaskId">The well-known mask its items wear.</param>
/// <param name="Extension">The item file extension, e.g. <c>.ics</c>.</param>
/// <param name="ContentType">The item media type served on GET.</param>
/// <param name="UidFieldName">The item mask's UID field — the resource name is derived from it.</param>
/// <param name="MultigetReport">The multiget REPORT element name.</param>
/// <param name="QueryReport">The query REPORT element name.</param>
/// <param name="DavCompliance">The extra token this protocol adds to the DAV: compliance header.</param>
internal sealed record DavProtocol(
    string BasePath,
    string CollectionsSegment,
    string Namespace,
    string NamespacePrefix,
    string HomeSetProperty,
    string CollectionResourceType,
    Guid FolderMaskId,
    Guid ItemMaskId,
    string Extension,
    string ContentType,
    string UidFieldName,
    string MultigetReport,
    string QueryReport,
    string DavCompliance)
{
    internal static readonly DavProtocol CalDav = new(
        BasePath: "/caldav",
        CollectionsSegment: "calendars",
        Namespace: "urn:ietf:params:xml:ns:caldav",
        NamespacePrefix: "C",
        HomeSetProperty: "calendar-home-set",
        CollectionResourceType: "calendar",
        FolderMaskId: WellKnownMaskIds.CalendarFolder,
        ItemMaskId: WellKnownMaskIds.Calendar,
        Extension: ".ics",
        ContentType: "text/calendar; charset=utf-8",
        UidFieldName: "Event UID",
        MultigetReport: "calendar-multiget",
        QueryReport: "calendar-query",
        DavCompliance: "calendar-access");

    internal static readonly DavProtocol CardDav = new(
        BasePath: "/carddav",
        CollectionsSegment: "addressbooks",
        Namespace: "urn:ietf:params:xml:ns:carddav",
        NamespacePrefix: "CARD",
        HomeSetProperty: "addressbook-home-set",
        CollectionResourceType: "addressbook",
        FolderMaskId: WellKnownMaskIds.ContactFolder,
        ItemMaskId: WellKnownMaskIds.Contact,
        Extension: ".vcf",
        ContentType: "text/vcard; charset=utf-8",
        UidFieldName: "Contact UID",
        MultigetReport: "addressbook-multiget",
        QueryReport: "addressbook-query",
        DavCompliance: "addressbook");

    internal static readonly IReadOnlyList<DavProtocol> All = [CalDav, CardDav];

    /// <summary>The protocol serving this request path, or null when the path is not a DAV one.</summary>
    internal static DavProtocol? ForPath(string path) =>
        All.FirstOrDefault(p =>
            path.Equals(p.BasePath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(p.BasePath + "/", StringComparison.OrdinalIgnoreCase));

    internal string PrincipalHref(Guid userId) => $"{BasePath}/principals/{userId}/";

    internal string HomeSetHref() => $"{BasePath}/{CollectionsSegment}/";

    internal string CollectionHref(Guid folderId) => $"{BasePath}/{CollectionsSegment}/{folderId}/";

    internal string ItemHref(Guid folderId, string resourceName) =>
        $"{BasePath}/{CollectionsSegment}/{folderId}/{Uri.EscapeDataString(resourceName)}";
}
