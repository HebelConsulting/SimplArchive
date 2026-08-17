// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using System.Xml.Linq;

namespace SimplArchive.Api.CalDav.Xml;

/// <summary>XML namespaces and element names for WebDAV / CardDAV (ported from SimplCalCon, ADR 0621).</summary>
public static class DavNames
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
    public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
    public static readonly XNamespace Push = "https://bitfire.at/webdav-push";

    // WebDAV-Push (ported from SimplCalCon, ADR 0621)
    public static readonly XName PushTransports = Push + "transports";
    public static readonly XName PushWebPush = Push + "web-push";
    public static readonly XName PushVapidPublicKey = Push + "vapid-public-key";
    public static readonly XName PushTopic = Push + "topic";
    public static readonly XName PushSupportedTriggers = Push + "supported-triggers";
    public static readonly XName PushContentUpdate = Push + "content-update";

    // DAV: core
    public static readonly XName Multistatus = Dav + "multistatus";
    public static readonly XName Response = Dav + "response";
    public static readonly XName Href = Dav + "href";
    public static readonly XName Propstat = Dav + "propstat";
    public static readonly XName Prop = Dav + "prop";
    public static readonly XName Status = Dav + "status";
    public static readonly XName Propfind = Dav + "propfind";
    public static readonly XName AllProp = Dav + "allprop";
    public static readonly XName PropName = Dav + "propname";

    public static readonly XName ResourceType = Dav + "resourcetype";
    public static readonly XName Collection = Dav + "collection";
    public static readonly XName Principal = Dav + "principal";
    public static readonly XName DisplayName = Dav + "displayname";
    public static readonly XName GetEtag = Dav + "getetag";
    public static readonly XName GetContentType = Dav + "getcontenttype";
    public static readonly XName GetContentLength = Dav + "getcontentlength";
    public static readonly XName CurrentUserPrincipal = Dav + "current-user-principal";
    public static readonly XName PrincipalUrl = Dav + "principal-URL";
    public static readonly XName Owner = Dav + "owner";
    public static readonly XName SupportedReportSet = Dav + "supported-report-set";
    public static readonly XName SupportedReport = Dav + "supported-report";
    public static readonly XName Report = Dav + "report";
    public static readonly XName SyncToken = Dav + "sync-token";
    public static readonly XName SyncCollection = Dav + "sync-collection";
    public static readonly XName SyncLevel = Dav + "sync-level";
    public static readonly XName CurrentUserPrivilegeSet = Dav + "current-user-privilege-set";
    public static readonly XName Privilege = Dav + "privilege";
    public static readonly XName Read = Dav + "read";
    public static readonly XName Write = Dav + "write";
    public static readonly XName WriteContent = Dav + "write-content";
    public static readonly XName WriteProperties = Dav + "write-properties";
    public static readonly XName Bind = Dav + "bind";
    public static readonly XName Unbind = Dav + "unbind";

    // CardDAV
    public static readonly XName AddressBook = CardDav + "addressbook";
    public static readonly XName AddressBookHomeSet = CardDav + "addressbook-home-set";
    public static readonly XName AddressBookQuery = CardDav + "addressbook-query";
    public static readonly XName AddressBookMultiget = CardDav + "addressbook-multiget";
    public static readonly XName SupportedAddressData = CardDav + "supported-address-data";
    public static readonly XName AddressDataType = CardDav + "address-data-type";
    public static readonly XName AddressData = CardDav + "address-data";

    // CalDAV
    public static readonly XName Calendar = CalDav + "calendar";
    public static readonly XName CalendarHomeSet = CalDav + "calendar-home-set";
    public static readonly XName CalendarQuery = CalDav + "calendar-query";
    public static readonly XName CalendarMultiget = CalDav + "calendar-multiget";
    public static readonly XName CalendarData = CalDav + "calendar-data";
    public static readonly XName SupportedCalendarData = CalDav + "supported-calendar-data";
    public static readonly XName SupportedCalendarComponentSet = CalDav + "supported-calendar-component-set";
    public static readonly XName Comp = CalDav + "comp";
    public static readonly XName CompFilter = CalDav + "comp-filter";
    public static readonly XName TimeRange = CalDav + "time-range";
    public static readonly XName CalFilter = CalDav + "filter";
    public static readonly XName CalPropFilter = CalDav + "prop-filter";
    public static readonly XName CalTextMatch = CalDav + "text-match";
    public static readonly XName CalIsNotDefined = CalDav + "is-not-defined";
    public static readonly XName CalParamFilter = CalDav + "param-filter";
    public static readonly XName CalComp = CalDav + "comp";
    public static readonly XName CalProp = CalDav + "prop";
    public static readonly XName CalExpand = CalDav + "expand";
    public static readonly XName CalLimitRecurrenceSet = CalDav + "limit-recurrence-set";
    public static readonly XName CalAllComp = CalDav + "allcomp";
    public static readonly XName CalAllProp = CalDav + "allprop";

    // CardDAV addressbook-query filter grammar (RFC 6352).
    public static readonly XName CardFilter = CardDav + "filter";
    public static readonly XName CardPropFilter = CardDav + "prop-filter";
    public static readonly XName CardTextMatch = CardDav + "text-match";
    public static readonly XName CardIsNotDefined = CardDav + "is-not-defined";
    public static readonly XName CardParamFilter = CardDav + "param-filter";
    public static readonly XName CardProp = CardDav + "prop";
    public static readonly XName CardAllProp = CardDav + "allprop";
    public static readonly XName CalendarTimeZone = CalDav + "calendar-timezone";

    // CalDAV scheduling (RFC 6638) + free-busy (RFC 4791)
    public static readonly XName CalendarUserAddressSet = CalDav + "calendar-user-address-set";
    public static readonly XName ScheduleInboxUrl = CalDav + "schedule-inbox-URL";
    public static readonly XName ScheduleOutboxUrl = CalDav + "schedule-outbox-URL";
    public static readonly XName ScheduleInbox = CalDav + "schedule-inbox";
    public static readonly XName ScheduleOutbox = CalDav + "schedule-outbox";
    public static readonly XName FreeBusyQuery = CalDav + "free-busy-query";
    public static readonly XName ScheduleResponse = CalDav + "schedule-response";
    public static readonly XName CalResponse = CalDav + "response";
    public static readonly XName Recipient = CalDav + "recipient";
    public static readonly XName RequestStatus = CalDav + "request-status";

    // calendarserver.org (CTag)
    public static readonly XName GetCTag = CalendarServer + "getctag";

    // ADDED for SimplArchive: the colour lives in Apple's ical namespace, which is where calendar and
    // contacts clients read it from (ADR 0620); getlastmodified is plain DAV: and simply was not needed there.
    public static readonly XNamespace AppleIcal = "http://apple.com/ns/ical/";

    public static readonly XName CalendarColor = AppleIcal + "calendar-color";

    public static readonly XName GetLastModified = Dav + "getlastmodified";

    public const string Ok = "HTTP/1.1 200 OK";
    public const string NotFound = "HTTP/1.1 404 Not Found";
}
