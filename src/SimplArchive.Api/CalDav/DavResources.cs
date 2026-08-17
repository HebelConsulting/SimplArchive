// PORTED from the sister project SimplCalCon's CalDavResources/CardDavResources (Apache-2.0, same licence) —
// see ADR 0621. MERGED into one: there they are two files because the two protocols are separate code paths;
// here they differ only in the constants a DavProtocol already carries, so this is one implementation and a
// divergence between the protocols is not expressible.
using System.Text;
using System.Xml.Linq;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.CalDav;

internal static class DavResources
{
    /// <summary>The service root, whose only job is to name the principal a client should read next.</summary>
    internal static DavResource Root(DavProtocol protocol, Guid userId)
    {
        var resource = new DavResource(protocol.BasePath + "/");
        resource.Set(DavNames.ResourceType, new XElement(DavNames.Collection));
        resource.Set(DavNames.CurrentUserPrincipal, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        resource.Set(DavNames.PrincipalUrl, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        return resource;
    }

    /// <summary>The principal, whose job is to name the home set.</summary>
    internal static DavResource Principal(DavProtocol protocol, Guid userId, string displayName)
    {
        var resource = new DavResource(protocol.PrincipalHref(userId));
        resource.Set(DavNames.ResourceType, new object[] { new XElement(DavNames.Collection), new XElement(DavNames.Principal) });
        resource.Set(DavNames.DisplayName, displayName);
        resource.Set(DavNames.CurrentUserPrincipal, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        resource.Set(DavNames.PrincipalUrl, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        resource.Set(protocol.HomeSetName, new XElement(DavNames.Href, protocol.HomeSetHref()));
        return resource;
    }

    /// <summary>The home set itself — a plain collection holding the subscribable ones.</summary>
    internal static DavResource Home(DavProtocol protocol, Guid userId)
    {
        var resource = new DavResource(protocol.HomeSetHref());
        resource.Set(DavNames.ResourceType, new XElement(DavNames.Collection));
        resource.Set(DavNames.DisplayName, protocol == DavProtocol.CalDav ? "Calendars" : "Addressbooks");
        resource.Set(DavNames.CurrentUserPrincipal, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        return resource;
    }

    /// <summary>One subscribable collection: a typed folder the caller can see.</summary>
    internal static DavResource Collection(
        DavProtocol protocol, Guid userId, DavCollection collection, EffectiveRights rights, long changeSequence = 0, string? vapidPublicKey = null)
    {
        var resource = new DavResource(protocol.CollectionHref(collection.FolderId));
        resource.Set(DavNames.ResourceType, new object[]
        {
            new XElement(DavNames.Collection),
            new XElement(protocol.CollectionTypeName),
        });
        resource.Set(DavNames.DisplayName, collection.DisplayName);
        resource.Set(DavNames.CurrentUserPrincipal, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        resource.Set(DavNames.Owner, new XElement(DavNames.Href, protocol.PrincipalHref(userId)));
        resource.Set(DavNames.SupportedReportSet, SupportedReports(protocol));
        // Both are the same number wearing different clothes: CTag is what a polling client compares, the
        // sync-token is what sync-collection resumes from (ADR 0622).
        resource.Set(DavNames.GetCTag, changeSequence.ToString());
        resource.Set(DavNames.SyncToken, DavTokens.Format(changeSequence));
        resource.Set(DavNames.CurrentUserPrivilegeSet, DavPrivileges.From(rights));

        if (protocol == DavProtocol.CalDav)
        {
            resource.Set(DavNames.SupportedCalendarComponentSet, new XElement(DavNames.Comp, new XAttribute("name", "VEVENT")));
            resource.Set(DavNames.SupportedCalendarData,
                new XElement(DavNames.CalendarData, new XAttribute("content-type", "text/calendar"), new XAttribute("version", "2.0")));
        }
        else
        {
            resource.Set(DavNames.SupportedAddressData,
                new XElement(DavNames.AddressDataType, new XAttribute("content-type", "text/vcard"), new XAttribute("version", "3.0")));
        }

        // WebDAV-Push (ported from SimplCalCon's DavPushAdvertisement, ADR 0622): the transport, this
        // collection's stable topic, and the triggers. Absent when push is off, which is how a client knows
        // to keep polling instead.
        if (vapidPublicKey is { Length: > 0 } vapid)
        {
            resource.Set(DavNames.PushTransports,
                new XElement(DavNames.PushWebPush,
                    new XElement(DavNames.PushVapidPublicKey, new XAttribute("type", "p256ecdsa"), vapid)));
            resource.Set(DavNames.PushTopic, PushTopic(collection.FolderId));
            resource.Set(DavNames.PushSupportedTriggers,
                new XElement(DavNames.PushContentUpdate, new XElement(DavNames.Dav + "depth", "1")));
        }

        // The colour, in the namespace calendar/contacts clients actually read it from (ADR 0620).
        if (collection.Color is { Length: > 0 } color)
        {
            resource.Set(DavNames.CalendarColor, color);
        }

        return resource;
    }

    /// <summary>One item. <paramref name="data"/> is the stored blob, omitted when only properties were asked for.</summary>
    internal static DavResource Item(DavProtocol protocol, DavItem item, string? data)
    {
        var resource = new DavResource(protocol.ItemHref(item.FolderId, item.ResourceName));
        resource.SetEmpty(DavNames.ResourceType);
        resource.Set(DavNames.GetEtag, $"\"{item.ETag}\"");
        resource.Set(DavNames.GetContentType, protocol.ContentType);
        resource.Set(DavNames.GetLastModified, item.LastModified.UtcDateTime.ToString("R"));
        if (item.SizeBytes is { } size)
        {
            resource.Set(DavNames.GetContentLength, size.ToString());
        }

        if (data is not null)
        {
            resource.Set(protocol == DavProtocol.CalDav ? DavNames.CalendarData : DavNames.AddressData, data);
        }

        return resource;
    }

    /// <summary>
    /// The stable, opaque push topic for a collection. Derived from the id rather than being the id: the topic
    /// travels to a third-party push service, which has no business learning our identifiers.
    /// </summary>
    private static string PushTopic(Guid folderId) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(folderId.ToByteArray()))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static IEnumerable<XElement> SupportedReports(DavProtocol protocol) =>
    [
        SupportedReport(protocol == DavProtocol.CalDav ? DavNames.CalendarQuery : DavNames.AddressBookQuery),
        SupportedReport(protocol == DavProtocol.CalDav ? DavNames.CalendarMultiget : DavNames.AddressBookMultiget),
        // Advertised now, answered in the sync slice — a client that sees it will prefer sync-collection over
        // re-listing, and seeing it absent is what makes some clients fall back to a full poll forever.
        SupportedReport(DavNames.SyncCollection),
    ];

    private static XElement SupportedReport(XName report) =>
        new(DavNames.SupportedReport, new XElement(DavNames.Report, new XElement(report)));
}
