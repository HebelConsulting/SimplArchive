using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared answer for showing and typing a document date's optional time (ADR "Optional time on the
// document date"; the zone half is #1254). Both clients render and parse through this, so the keyboard-first
// spellings, the display shape, and the UTC↔viewer's-zone round trip are pinned here once.
public class DocumentDateFormatTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // By IANA id, which is what the preference stores and what a browser reports. On a host with no zone
    // database this throws rather than silently answering — the honest outcome for a test that would otherwise
    // be reporting on a machine that cannot do the thing under test.
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Fact]
    public void Display_shows_the_date_alone_when_there_is_no_time()
    {
        Assert.Equal("2026-09-06", DocumentDateFormat.Display(new DateOnly(2026, 9, 6), null, Utc));
        Assert.Equal("2026-09-06", DocumentDateFormat.Display("2026-09-06", null, Utc));
        Assert.Equal("2026-09-06", DocumentDateFormat.Display("2026-09-06", string.Empty, Utc));
    }

    [Fact]
    public void A_date_with_no_time_is_NOT_converted_even_in_a_distant_zone()
    {
        // The exception that must hold in both directions. "Filed on the 6th" is a calendar day, not an
        // instant; converting it would make the document read as the 5th for anyone west of the server —
        // inventing a bug in order to fix one that was never there.
        Assert.Equal("2026-09-06", DocumentDateFormat.Display(new DateOnly(2026, 9, 6), null, Zurich));
        Assert.Equal((new DateOnly(2026, 9, 6), (TimeOnly?)null), DocumentDateFormat.ToZone(new DateOnly(2026, 9, 6), null, Zurich));
        Assert.Equal((new DateOnly(2026, 9, 6), (TimeOnly?)null), DocumentDateFormat.ToUtc(new DateOnly(2026, 9, 6), null, Zurich));
    }

    [Fact]
    public void Display_appends_the_utc_time_when_the_viewer_reads_in_UTC()
    {
        Assert.Equal("2026-09-06 09:50 UTC", DocumentDateFormat.Display(new DateOnly(2026, 9, 6), new TimeOnly(9, 50), Utc));
        Assert.Equal("2026-09-06 09:50 UTC", DocumentDateFormat.Display("2026-09-06", "09:50", Utc));
    }

    [Fact]
    public void Display_moves_the_time_into_the_viewers_zone_and_says_which()
    {
        // September is CEST (+02:00), so 09:50 UTC reads 11:50 to somebody in Zurich. The marker is an OFFSET
        // rather than an abbreviation because .NET cannot portably produce "CEST" — and it is not optional:
        // a bare "11:50" in a screenshot is a number nobody outside the reporter's zone can place.
        Assert.Equal("2026-09-06 11:50 +02:00", DocumentDateFormat.Display(new DateOnly(2026, 9, 6), new TimeOnly(9, 50), Zurich));
        Assert.Equal("2026-09-06 11:50 +02:00", DocumentDateFormat.Display("2026-09-06", "09:50", Zurich));
    }

    [Fact]
    public void Display_moves_the_DATE_too_when_the_time_crosses_midnight()
    {
        // THE HALF THAT IS EASY TO MISS: the two columns are ONE instant, so a converted time beside an
        // unconverted date is a row whose halves disagree. 22:30 UTC on the 5th is 00:30 on the 6th in Zurich.
        Assert.Equal("2026-09-06 00:30 +02:00", DocumentDateFormat.Display(new DateOnly(2026, 9, 5), new TimeOnly(22, 30), Zurich));
    }

    [Fact]
    public void The_marker_follows_the_INSTANT_not_the_zones_base_offset()
    {
        // A summer and a winter document sit in the same list and must not claim the same offset.
        Assert.Equal("+02:00", DocumentDateFormat.Marker(new DateOnly(2026, 7, 15), new TimeOnly(9, 0), Zurich));
        Assert.Equal("+01:00", DocumentDateFormat.Marker(new DateOnly(2026, 1, 15), new TimeOnly(9, 0), Zurich));

        // A zone at a zero offset says UTC rather than "+00:00" — which for London in winter is exactly right.
        Assert.Equal("UTC", DocumentDateFormat.Marker(new DateOnly(2026, 1, 15), new TimeOnly(9, 0), TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));
    }

    [Fact]
    public void A_negative_offset_is_signed()
    {
        // The sign is formatted by hand (TimeSpan's "hh" has no sign), so a western zone is worth asserting:
        // "05:00" where "-05:00" was meant is a marker that says the opposite of the truth.
        Assert.StartsWith("-", DocumentDateFormat.Marker(new DateOnly(2026, 1, 15), new TimeOnly(9, 0), TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
    }

    [Theory]
    [InlineData(2026, 7, 15, 8, 0)]    // summer, +02:00
    [InlineData(2026, 1, 15, 8, 0)]    // winter, +01:00
    [InlineData(2026, 3, 29, 0, 30)]   // the night the clocks go forward
    [InlineData(2026, 9, 5, 22, 30)]   // crosses midnight into the next local day
    public void A_stored_pair_survives_a_round_trip_through_the_viewers_zone(int y, int mo, int d, int h, int mi)
    {
        // The property that actually matters, asserted as a round trip rather than as two separate conversions:
        // showing local while saving the typed value verbatim shifts the document by the offset EVERY time
        // somebody opens and saves it, and each individual save looks correct.
        var (date, time) = (new DateOnly(y, mo, d), new TimeOnly(h, mi));

        var (localDate, localTime) = DocumentDateFormat.ToZone(date, time, Zurich);
        var (backDate, backTime) = DocumentDateFormat.ToUtc(localDate, localTime, Zurich);

        Assert.Equal(date, backDate);
        Assert.Equal(time, backTime);
    }

    [Fact]
    public void The_one_hour_a_year_that_CANNOT_round_trip_resolves_to_standard_time()
    {
        // NOT A DEFECT, and worth pinning so nobody "fixes" it. On 25 October 2026 Zurich repeats 02:00–03:00,
        // so two distinct UTC instants — 00:30 and 01:30 — are both 02:30 local. A wall clock cannot say which,
        // and no implementation can recover information the display never carried: this is the cost of showing
        // a local time at all, not a bug in the conversion.
        //
        // The round-trip theory above deliberately EXCLUDES this hour rather than asserting something weaker
        // across the board. It was in that theory first and failed, which is how this case got written.
        var (localDate, localTime) = DocumentDateFormat.ToZone(new DateOnly(2026, 10, 25), new TimeOnly(0, 30), Zurich);

        Assert.Equal(new DateOnly(2026, 10, 25), localDate);
        Assert.Equal(new TimeOnly(2, 30), localTime);   // still CEST, +02:00

        // Typed back, 02:30 resolves to STANDARD time (+01:00) — .NET's documented rule for an ambiguous wall
        // clock, and the deterministic half of a genuinely ambiguous question.
        var (backDate, backTime) = DocumentDateFormat.ToUtc(localDate, localTime, Zurich);

        Assert.Equal(new DateOnly(2026, 10, 25), backDate);
        Assert.Equal(new TimeOnly(1, 30), backTime);
    }

    [Fact]
    public void The_edit_FIELDS_round_trip_in_the_shapes_the_panes_actually_bind()
    {
        // The plumbing overload, because both clients bind a DateTime? picker and a typed string — and the
        // conversion being right on DateOnly/TimeOnly says nothing about the adapter the panes really call.
        var (localDate, localTime) = DocumentDateFormat.FieldsInZone(new DateTime(2026, 9, 5), "22:30", Zurich);

        Assert.Equal(new DateTime(2026, 9, 6), localDate);
        Assert.Equal("00:30", localTime);

        var (wireDate, wireTime) = DocumentDateFormat.FieldsInUtc(localDate, localTime, Zurich);

        Assert.Equal("2026-09-05", wireDate);
        Assert.Equal("22:30", wireTime);
    }

    [Fact]
    public void The_edit_fields_leave_a_date_only_pair_alone()
    {
        Assert.Equal((new DateTime(2026, 9, 6), (string?)null), DocumentDateFormat.FieldsInZone(new DateTime(2026, 9, 6), null, Zurich));
        Assert.Equal(("2026-09-06", (string?)null), DocumentDateFormat.FieldsInUtc(new DateTime(2026, 9, 6), null, Zurich));

        // A blank time is the "delete the time" state, and it must not be mistaken for a value to convert.
        Assert.Equal((new DateTime(2026, 9, 6), string.Empty), DocumentDateFormat.FieldsInZone(new DateTime(2026, 9, 6), string.Empty, Zurich));
    }

    [Theory]
    [InlineData("09:50", 9, 50)]
    [InlineData("9:50", 9, 50)]
    [InlineData("0950", 9, 50)]
    [InlineData("950", 9, 50)]
    [InlineData("9.50", 9, 50)]
    [InlineData("23:59", 23, 59)]
    [InlineData("0000", 0, 0)]
    public void TryParseTypedTime_accepts_the_obvious_spellings(string typed, int h, int m)
    {
        Assert.True(DocumentDateFormat.TryParseTypedTime(typed, out var time));
        Assert.Equal(new TimeOnly(h, m), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseTypedTime_treats_blank_as_no_time(string? typed)
    {
        Assert.True(DocumentDateFormat.TryParseTypedTime(typed, out var time)); // blank is a SUCCESSFUL "no time"
        Assert.Null(time);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("2500")]
    [InlineData("nonsense")]
    [InlineData("9")]
    [InlineData("99999")]
    public void TryParseTypedTime_rejects_a_present_but_invalid_time(string typed)
    {
        Assert.False(DocumentDateFormat.TryParseTypedTime(typed, out var time));
        Assert.Null(time);
    }

    [Fact]
    public void FormatTime_round_trips_the_wire_form()
    {
        Assert.Equal("09:50", DocumentDateFormat.FormatTime(new TimeOnly(9, 50)));
        Assert.Null(DocumentDateFormat.FormatTime(null));
    }
}
