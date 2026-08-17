// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using System.Globalization;
using System.Xml.Linq;


namespace SimplArchive.Api.CalDav.Xml;

/// <summary>
/// Parses CalDAV <c>calendar-query</c> and CardDAV <c>addressbook-query</c> filters
/// (RFC 4791 §9.7 / RFC 6352 §10.5) into the provider-agnostic query model (ported from SimplCalCon, ADR 0621).
/// v1: comp-filter component + time-range, prop-filter with text-match / is-not-defined, allof/anyof.
/// </summary>
internal static class DavFilterParser
{
    public static CalendarQueryFilter ParseCalendarQuery(XElement queryBody)
    {
        var filter = queryBody.Element(DavNames.CalFilter);
        var vcalendar = filter?.Element(DavNames.CompFilter);    // comp-filter name="VCALENDAR"
        var component = vcalendar?.Element(DavNames.CompFilter);  // comp-filter name="VEVENT" / "VTODO"
        var scope = component ?? vcalendar;

        var timeRange = scope?.Element(DavNames.TimeRange);
        var start = ParseIcalUtc(timeRange?.Attribute("start")?.Value);
        var end = ParseIcalUtc(timeRange?.Attribute("end")?.Value);

        var props = (scope?.Elements(DavNames.CalPropFilter) ?? Enumerable.Empty<XElement>())
            .Select(pf => ParsePropFilter(pf, DavNames.CalTextMatch, DavNames.CalIsNotDefined, DavNames.CalParamFilter, caldav: true))
            .ToList();

        return new CalendarQueryFilter(component?.Attribute("name")?.Value, start, end, props);
    }

    public static ContactQueryFilter ParseAddressbookQuery(XElement queryBody)
    {
        var filter = queryBody.Element(DavNames.CardFilter);
        if (filter is null)
        {
            return ContactQueryFilter.MatchAll;
        }

        // RFC 6352: the filter test defaults to "anyof".
        var test = string.Equals(filter.Attribute("test")?.Value, "allof", StringComparison.OrdinalIgnoreCase)
            ? FilterTest.AllOf
            : FilterTest.AnyOf;

        var props = filter.Elements(DavNames.CardPropFilter)
            .Select(pf => ParsePropFilter(pf, DavNames.CardTextMatch, DavNames.CardIsNotDefined, DavNames.CardParamFilter, caldav: false))
            .ToList();

        return new ContactQueryFilter(test, props);
    }

    private static DavPropFilter ParsePropFilter(
        XElement pf, XName textMatchName, XName isNotDefinedName, XName paramFilterName, bool caldav)
    {
        var name = pf.Attribute("name")?.Value ?? string.Empty;
        if (pf.Element(isNotDefinedName) is not null)
        {
            return new DavPropFilter(name, IsNotDefined: true, TextMatch: null);
        }

        var textMatch = ParseTextMatch(pf.Element(textMatchName), caldav);
        var paramFilters = pf.Elements(paramFilterName)
            .Select(param => ParseParamFilter(param, textMatchName, isNotDefinedName, caldav))
            .ToList();

        return new DavPropFilter(name, IsNotDefined: false, textMatch, paramFilters.Count > 0 ? paramFilters : null);
    }

    private static DavParamFilter ParseParamFilter(XElement param, XName textMatchName, XName isNotDefinedName, bool caldav)
    {
        var name = param.Attribute("name")?.Value ?? string.Empty;
        if (param.Element(isNotDefinedName) is not null)
        {
            return new DavParamFilter(name, IsNotDefined: true, TextMatch: null);
        }

        return new DavParamFilter(name, IsNotDefined: false, ParseTextMatch(param.Element(textMatchName), caldav));
    }

    private static DavTextMatch? ParseTextMatch(XElement? textMatch, bool caldav)
    {
        if (textMatch is null)
        {
            return null;
        }

        var negate = string.Equals(textMatch.Attribute("negate-condition")?.Value, "yes", StringComparison.OrdinalIgnoreCase);
        // CalDAV text-match is always a substring match; CardDAV carries an explicit match-type.
        var matchType = caldav ? TextMatchType.Contains : ParseMatchType(textMatch.Attribute("match-type")?.Value);
        return new DavTextMatch(textMatch.Value, matchType, negate);
    }

    private static TextMatchType ParseMatchType(string? value) => value?.ToLowerInvariant() switch
    {
        "equals" => TextMatchType.Equals,
        "starts-with" => TextMatchType.StartsWith,
        "ends-with" => TextMatchType.EndsWith,
        _ => TextMatchType.Contains,
    };

    private static DateTime? ParseIcalUtc(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        foreach (var format in (string[])["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"])
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }

        return null;
    }
}
