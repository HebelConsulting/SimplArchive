using System.Reflection;
using System.Text.Json;
using SimplArchive.Api.Serialization;

namespace SimplArchive.UnitTests;

// The API deals in INSTANTS, written as Zulu — and the two exceptions are named rather than discovered (ADR 0802).
//
// WHY A GUARD AND NOT JUST AN ADR. The failure this protects against is invisible to the person who introduces
// it, which is the same property that let ADR 0548's bug survive a full green suite: every test in this
// repository writes UtcNow and every runner is UTC, so an endpoint that mishandles offsets passes everything
// and is broken only for callers east or west of Greenwich. A rule that can only be broken by someone who has
// read the rule is not a rule — it is a note.
public class ApiTimeContractTests
{
    // The two shapes on this API that are deliberately NOT instants, plus the CalDAV internals that already say
    // UTC in their own names. Each entry is a decision with an ADR behind it, not an exemption granted to make
    // this test pass — which is the distinction that keeps an allowlist honest.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // A wall clock PLUS a TZID, and the pair is the intent. Collapsing it to an instant makes "every Monday
        // 09:00 Zurich" drift by an hour at the daylight-saving change — the entry would keep its instant and
        // lose its meaning (ADRs 0631, 0690). A floating time has no zone at all, so there is nothing to
        // convert from; stamping one is the bug, not the fix.
        // Each of these four sits in a type that ALSO carries StartTimeZoneId/EndTimeZoneId — the zone travels
        // beside the clock, which is what makes the pair lossless and the instant lossy.
        ["SimplArchive.Api.Controllers.DocumentAppointmentController+AppointmentResource.Start"] =
            "wall clock + TZID (ADR 0690)",
        ["SimplArchive.Api.Controllers.DocumentAppointmentController+AppointmentResource.End"] =
            "wall clock + TZID (ADR 0690)",
        ["SimplArchive.Api.Documents.Appointment.Start"] = "wall clock + TZID (ADR 0690)",
        ["SimplArchive.Api.Documents.Appointment.End"] = "wall clock + TZID (ADR 0690)",

        // CalDAV's own XML contracts, not the JSON surface — and RFC 4791 defines a calendar-query time-range
        // as UTC, so these are already instants by the protocol's own rule. Named ...Utc at the property, which
        // is the cheapest possible way of saying so.
        ["SimplArchive.Api.CalDav.Xml.ExpandWindow.StartUtc"] = "CalDAV internal window, UTC by name",
        ["SimplArchive.Api.CalDav.Xml.ExpandWindow.EndUtc"] = "CalDAV internal window, UTC by name",
        ["SimplArchive.Api.CalDav.Xml.RecurrenceLimit.StartUtc"] = "CalDAV internal window, UTC by name",
        ["SimplArchive.Api.CalDav.Xml.RecurrenceLimit.EndUtc"] = "CalDAV internal window, UTC by name",
        ["SimplArchive.Api.CalDav.Xml.CalendarQueryFilter.StartUtc"] = "RFC 4791 time-range, UTC by the spec",
        ["SimplArchive.Api.CalDav.Xml.CalendarQueryFilter.EndUtc"] = "RFC 4791 time-range, UTC by the spec",
    };

    [Fact]
    public void A_written_timestamp_says_Z_rather_than_an_offset_of_zero()
    {
        // Both spellings are the same instant, so this is not about correctness — it is about whether the rule
        // can be SEEN. "+00:00" reads as "an offset that happens to be zero", which is exactly what a value
        // that escaped normalisation would also look like.
        var json = JsonSerializer.Serialize(
            new DateTimeOffset(2026, 9, 17, 9, 14, 35, TimeSpan.Zero), Options());

        Assert.EndsWith("Z\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_in_another_zone_is_written_as_the_same_instant_in_Zulu()
    {
        // The half that would be worth nothing if the converter merely relabelled: 23:59+02:00 is 21:59Z, a
        // different clock reading and the same moment. An implementation that stamped "Z" onto the local wall
        // clock would pass the test above and fail this one.
        var json = JsonSerializer.Serialize(
            new DateTimeOffset(2026, 9, 6, 23, 59, 0, TimeSpan.FromHours(2)), Options());

        Assert.StartsWith("\"2026-09-06T21:59:00", json, StringComparison.Ordinal);
        Assert.EndsWith("Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timestamp_arriving_with_a_local_offset_is_read_as_the_same_instant()
    {
        // ADR 0548's case, asserted locally and cheaply. Deliberately a NON-ZERO offset: a test written with
        // UtcNow asserts nothing about offset handling, which is precisely how the original defect shipped.
        var read = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-09-06T23:59:00+02:00\"", Options());

        Assert.Equal(TimeSpan.Zero, read.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 21, 59, 0, TimeSpan.Zero), read);
    }

    [Fact]
    public void No_new_API_type_introduces_a_bare_DateTime_without_saying_why()
    {
        // A bare DateTime carries no offset and no Kind that survives JSON, so it cannot say which moment it
        // means — which is fine for the two shapes above, where the zone travels in a separate field, and wrong
        // everywhere else. DateTimeOffset is the default answer; this fails when something else appears.
        var offenders = typeof(UtcDateTimeOffsetConverter).Assembly
            .GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => Unwrap(property.PropertyType) == typeof(DateTime))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .Where(name => !Allowed.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These API properties are typed `DateTime`, which cannot say which moment it means once it is JSON "
            + "— no offset, and no Kind that survives the round trip (ADR 0802):\n  "
            + string.Join("\n  ", offenders)
            + "\n\nUse DateTimeOffset, which the boundary converter normalises to UTC and writes as Zulu. If the "
            + "value genuinely is NOT an instant — a wall clock that travels with its own zone field, as an "
            + "appointment's Start/End does — add it to `Allowed` above WITH THE REASON, and say so at the "
            + "property too. An entry here is a decision somebody made, not a way to make this test quiet.");
    }

    [Fact]
    public void The_allowlist_names_only_properties_that_still_exist()
    {
        // The other direction, and the one an allowlist always rots in: a stale entry silently widens the rule,
        // and nothing ever points at it. Same shape as the workaround-dependency rule — an exception outlives
        // its reason unless something checks.
        var live = typeof(UtcDateTimeOffsetConverter).Assembly
            .GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Keys.Where(name => !live.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "These allowlist entries name properties that no longer exist — remove them, or the exception "
            + "outlives the reason it was granted for:\n  " + string.Join("\n  ", stale));
    }

    // ---- The XML representation (#1259) ------------------------------------------------------------------
    //
    // The converter above is a System.Text.Json one, so it covers the JSON surface and NOTHING ELSE. The XML
    // formatter (ADR 0190) has its own pipeline and runs no STJ converters, so the rule held on one negotiated
    // representation and silently did not hold on the other — a note, not a rule, in ADR 0802's own terms.
    //
    // MEASURED BEFORE FIXING, and it corrected the expectation: `XmlSerializer` already writes a zero-offset
    // value as `...35Z` rather than `+00:00`, so the OUTBOUND half was never broken on this path. Only the
    // inbound half was, and normalising there is what makes the outbound half true as a consequence.

    // PUBLIC, and not by preference: XmlSerializer refuses a non-public type outright ("Only public types can
    // be processed"), which is the same constraint that makes every DTO on this API a plain public class with a
    // parameterless constructor. A probe that did not share that shape would not be probing the real thing.
    public sealed class XmlProbe
    {
        public DateTimeOffset At { get; set; }

        public DateTimeOffset? MaybeAt { get; set; }

        public XmlProbeChild? Child { get; set; }

        public List<XmlProbeChild> Children { get; set; } = [];
    }

    public sealed class XmlProbeChild
    {
        public DateTimeOffset At { get; set; }
    }

    [Fact]
    public void An_inbound_XML_timestamp_is_normalised_to_the_same_instant_in_UTC()
    {
        // A NON-ZERO offset, deliberately: a fixture written with UtcNow asserts nothing about offset handling,
        // which is the documented reason this class of bug survived a full green suite the first time.
        var model = new XmlProbe
        {
            At = new DateTimeOffset(2026, 9, 17, 11, 14, 35, TimeSpan.FromHours(2)),
            MaybeAt = new DateTimeOffset(2026, 9, 17, 11, 14, 35, TimeSpan.FromHours(2)),
            Child = new XmlProbeChild { At = new DateTimeOffset(2026, 9, 17, 11, 14, 35, TimeSpan.FromHours(2)) },
            Children = [new XmlProbeChild { At = new DateTimeOffset(2026, 9, 17, 11, 14, 35, TimeSpan.FromHours(2)) }],
        };

        UtcModelNormalizer.Normalize(model);

        var expected = new DateTimeOffset(2026, 9, 17, 9, 14, 35, TimeSpan.Zero);

        // The INSTANT is unchanged — this is normalisation, not truncation — and the offset is now zero, which
        // is the only thing Npgsql will store for `timestamp with time zone`.
        Assert.Equal(expected, model.At);
        Assert.Equal(TimeSpan.Zero, model.At.Offset);

        // Nullable, nested and collection members too: the bug is not a property of the top-level type, and a
        // walk that stopped at the surface would leave a request that fails exactly as before.
        Assert.Equal(TimeSpan.Zero, model.MaybeAt!.Value.Offset);
        Assert.Equal(expected, model.MaybeAt!.Value);
        Assert.Equal(TimeSpan.Zero, model.Child!.At.Offset);
        Assert.Equal(TimeSpan.Zero, model.Children[0].At.Offset);
    }

    [Fact]
    public void A_normalised_model_is_then_written_as_Zulu_by_the_XML_serializer()
    {
        // The outbound half, asserted rather than assumed — and it is a CONSEQUENCE of the inbound fix rather
        // than a second mechanism: XmlSerializer spells a zero offset `Z` on its own.
        var model = new XmlProbe { At = new DateTimeOffset(2026, 9, 17, 11, 14, 35, TimeSpan.FromHours(2)) };
        UtcModelNormalizer.Normalize(model);

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(XmlProbe));
        using var writer = new StringWriter();
        serializer.Serialize(writer, model);
        var xml = writer.ToString();

        Assert.Contains("2026-09-17T09:14:35Z", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("+02:00", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", xml, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions Options() => new() { Converters = { new UtcDateTimeOffsetConverter() } };

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;
}
