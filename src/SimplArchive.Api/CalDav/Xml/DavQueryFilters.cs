// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
namespace SimplArchive.Api.CalDav.Xml;

/// <summary>How a set of prop-filters combine (RFC 6352/4791 <c>test</c>).</summary>
public enum FilterTest
{
    AllOf,
    AnyOf,
}

/// <summary>text-match match modes (RFC 6352; CalDAV text-match is always <see cref="Contains"/>).</summary>
public enum TextMatchType
{
    Contains,
    Equals,
    StartsWith,
    EndsWith,
}

/// <summary>A CardDAV/CalDAV <c>text-match</c> on a property value (case-insensitive).</summary>
public sealed record DavTextMatch(string Value, TextMatchType MatchType, bool Negate);

/// <summary>
/// A <c>prop-filter</c>: the property must be absent (<see cref="IsNotDefined"/>), present (no
/// <see cref="TextMatch"/>), or present with a value that text-matches. Any <see cref="Params"/>
/// (param-filters, RFC 4791/6352) must additionally all match on the same property occurrence.
/// </summary>
public sealed record DavPropFilter(
    string Name, bool IsNotDefined, DavTextMatch? TextMatch, IReadOnlyList<DavParamFilter>? Params = null);

/// <summary>A <c>param-filter</c>: a named property parameter must be absent (<see cref="IsNotDefined"/>), present, or text-match.</summary>
public sealed record DavParamFilter(string Name, bool IsNotDefined, DavTextMatch? TextMatch);

/// <summary>A CardDAV addressbook-query filter over vCard properties.</summary>
public sealed record ContactQueryFilter(FilterTest Test, IReadOnlyList<DavPropFilter> Props)
{
    public static readonly ContactQueryFilter MatchAll = new(FilterTest.AllOf, []);
}

/// <summary>A CalDAV calendar-query filter: a component + time-range + prop-filters (RFC 4791 = allof).</summary>
public sealed record CalendarQueryFilter(
    string? Component, DateTime? StartUtc, DateTime? EndUtc, IReadOnlyList<DavPropFilter> Props)
{
    public static CalendarQueryFilter TimeRange(DateTime? startUtc, DateTime? endUtc) =>
        new(null, startUtc, endUtc, []);
}
