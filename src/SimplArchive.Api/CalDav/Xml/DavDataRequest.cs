// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using System.Globalization;
using System.Xml.Linq;


namespace SimplArchive.Api.CalDav.Xml;

/// <summary>
/// Parses a <c>calendar-data</c>/<c>address-data</c> request element (RFC 4791 §9.6 / RFC 6352
/// §10.4) into a provider-agnostic <see cref="CalendarDataRequest"/>/<see cref="AddressDataRequest"/>
/// so Infrastructure can subset + expand the blob (ported from SimplCalCon, ADR 0621). Absent/empty element → the full object.
/// </summary>
internal static class DavDataRequest
{
    public static CalendarDataRequest ParseCalendarData(XElement? calendarData)
    {
        if (calendarData is null)
        {
            return CalendarDataRequest.Full;
        }

        // At most one top-level <comp name="VCALENDAR"> (RFC 4791 §9.6.1); parse it into a component tree.
        var rootComp = calendarData.Element(DavNames.CalComp);
        var root = rootComp is null ? null : ParseComp(rootComp);

        var expandElement = calendarData.Element(DavNames.CalExpand);
        var expand = expandElement is not null
            && ParseUtc(expandElement.Attribute("start")?.Value) is { } start
            && ParseUtc(expandElement.Attribute("end")?.Value) is { } end
            ? new ExpandWindow(start, end)
            : null;

        var limitElement = calendarData.Element(DavNames.CalLimitRecurrenceSet);
        var limit = limitElement is not null
            && ParseUtc(limitElement.Attribute("start")?.Value) is { } limitStart
            && ParseUtc(limitElement.Attribute("end")?.Value) is { } limitEnd
            ? new RecurrenceLimit(limitStart, limitEnd)
            : null;

        return new CalendarDataRequest(root, expand, limit);
    }

    public static AddressDataRequest ParseAddressData(XElement? addressData)
    {
        if (addressData is null)
        {
            return AddressDataRequest.Full;
        }

        var props = addressData.Elements(DavNames.CardProp)
            .Select(p => p.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new AddressDataRequest(props);
    }

    // Parse one <comp> into a selection node, recursing into nested <comp> children (ported from SimplCalCon, ADR 0621).
    private static DavCompSelection ParseComp(XElement comp)
    {
        var propElements = comp.Elements(DavNames.CalProp).ToList();
        var childComps = comp.Elements(DavNames.CalComp).ToList();
        var hasAllProp = comp.Element(DavNames.CalAllProp) is not null;
        var hasAllComp = comp.Element(DavNames.CalAllComp) is not null;

        // RFC 4791: a comp with no children returns all properties and all sub-components.
        var noChildren = propElements.Count == 0 && childComps.Count == 0 && !hasAllProp && !hasAllComp;

        var props = propElements
            .Select(p => p.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var comps = new Dictionary<string, DavCompSelection>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in childComps)
        {
            if (child.Attribute("name")?.Value is { Length: > 0 } childName)
            {
                comps[childName] = ParseComp(child);
            }
        }

        return new DavCompSelection(hasAllProp || noChildren, hasAllComp || noChildren, props, comps);
    }

    private static DateTime? ParseUtc(string? value)
    {
        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];
        return !string.IsNullOrEmpty(value)
            && DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }
}
