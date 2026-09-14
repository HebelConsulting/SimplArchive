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
/// <param name="Extension">The item file extension, e.g. <c>.ics</c>.</param>
/// <param name="ContentType">The item media type served on GET.</param>
/// <param name="UidFieldName">The item mask's UID field — the resource name is derived from it.</param>
/// <param name="MultigetReport">The multiget REPORT element name.</param>
/// <param name="QueryReport">The query REPORT element name.</param>
/// <param name="DavCompliance">The extra token this protocol adds to the DAV: compliance header.</param>
/// <remarks>
/// The WIRE half only. Which masks a protocol's collections and items wear is NOT here (ABI 0.24, ADR 0791):
/// modules declare .ics collections of their own (a flight-log Logbook), known only at startup, so the mask
/// sets come from <see cref="Application.Abstractions.IDavCollectionKindRegistry"/> — keyed by
/// <see cref="Extension"/> — rather than a static list a module could never reach. The extension and UID
/// field DO belong here: one protocol, one item grammar, and every .ics kind shares it.
/// </remarks>
internal sealed record DavProtocol(
    string BasePath,
    string CollectionsSegment,
    string Namespace,
    string NamespacePrefix,
    string HomeSetProperty,
    string CollectionResourceType,
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
        Extension: Domain.CalDav.DavCollectionKinds.Calendar.Extension,
        ContentType: "text/calendar; charset=utf-8",
        UidFieldName: Domain.CalDav.DavCollectionKinds.Calendar.UidFieldName,
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
        Extension: Domain.CalDav.DavCollectionKinds.Addressbook.Extension,
        ContentType: "text/vcard; charset=utf-8",
        UidFieldName: Domain.CalDav.DavCollectionKinds.Addressbook.UidFieldName,
        MultigetReport: "addressbook-multiget",
        QueryReport: "addressbook-query",
        DavCompliance: "addressbook");

    internal static readonly IReadOnlyList<DavProtocol> All = [CalDav, CardDav];

    /// <summary>The protocol serving this request path, or null when the path is not a DAV one.</summary>
    internal static DavProtocol? ForPath(string path) =>
        All.FirstOrDefault(p =>
            path.Equals(p.BasePath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(p.BasePath + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The home-set property as an XName, for the ported XML builders.</summary>
    internal System.Xml.Linq.XName HomeSetName =>
        (this == CalDav ? Xml.DavNames.CalDav : Xml.DavNames.CardDav) + HomeSetProperty;

    /// <summary>The element marking a collection as this protocol's kind, as an XName.</summary>
    internal System.Xml.Linq.XName CollectionTypeName =>
        (this == CalDav ? Xml.DavNames.CalDav : Xml.DavNames.CardDav) + CollectionResourceType;

    internal string PrincipalHref(Guid userId) => $"{BasePath}/principals/{userId}/";

    internal string HomeSetHref() => $"{BasePath}/{CollectionsSegment}/";

    internal string CollectionHref(Guid folderId) => $"{BasePath}/{CollectionsSegment}/{folderId}/";

    internal string ItemHref(Guid folderId, string resourceName) =>
        $"{BasePath}/{CollectionsSegment}/{folderId}/{Uri.EscapeDataString(resourceName)}";
}
