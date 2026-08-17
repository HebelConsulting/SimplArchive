// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
namespace SimplArchive.Api.CalDav.Xml;

/// <summary>
/// A parsed CalDAV <c>calendar-data</c> request (RFC 4791 §9.6): the requested component tree (rooted
/// at VCALENDAR) plus optional recurrence <c>expand</c>/<c>limit-recurrence-set</c> (ported, ADR 0621).
/// A null <see cref="Root"/> and no expand/limit = the full object.
/// </summary>
public sealed record CalendarDataRequest(
    DavCompSelection? Root,
    ExpandWindow? Expand,
    RecurrenceLimit? Limit = null)
{
    public static readonly CalendarDataRequest Full = new(null, null);

    /// <summary>Nothing to do — return the blob unchanged.</summary>
    public bool IsFull => Root is null && Expand is null && Limit is null;
}

/// <summary>
/// The properties and <b>nested</b> sub-components to keep for one component (RFC 4791 <c>comp</c>,
/// ADR 0073). <see cref="Comps"/> holds the requested child components keyed by name, each with its
/// own selection — so the tree is honored to any depth (e.g. VALARM under VEVENT vs under VTODO).
/// </summary>
public sealed record DavCompSelection(
    bool AllProps, bool AllComps, IReadOnlySet<string> Props, IReadOnlyDictionary<string, DavCompSelection> Comps);

/// <summary>A recurrence-expansion window: return one component per occurrence starting in [Start, End).</summary>
public sealed record ExpandWindow(DateTime StartUtc, DateTime EndUtc);

/// <summary>A limit-recurrence-set window (RFC 4791 §9.6.5): keep the master + only overrides overlapping [Start, End).</summary>
public sealed record RecurrenceLimit(DateTime StartUtc, DateTime EndUtc);

/// <summary>A parsed CardDAV <c>address-data</c> request (RFC 6352 §10.4): which vCard properties to return.</summary>
public sealed record AddressDataRequest(IReadOnlySet<string> Props)
{
    public static readonly AddressDataRequest Full = new(new HashSet<string>());

    public bool IsFull => Props.Count == 0;
}
