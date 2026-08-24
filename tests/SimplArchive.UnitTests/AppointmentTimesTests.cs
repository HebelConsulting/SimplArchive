using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The three readings of an appointment's time (ADR 0690): UTC, as recorded, and — only when it would say
// something new — in the reader's own zone.
public class AppointmentTimesTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
    private static readonly TimeZoneInfo Boston = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void An_entry_in_the_readers_own_zone_needs_no_third_block()
    {
        var times = AppointmentTimes.For(
            new DateTime(2026, 8, 24, 11, 0, 0), new DateTime(2026, 8, 24, 12, 0, 0),
            isAllDay: false, "Europe/Zurich", "Europe/Zurich", Zurich);

        Assert.NotNull(times);

        // Summer: Zurich is UTC+2, so 11:00 local is 09:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero), times!.Utc.Start);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 11, 0, 0, TimeSpan.FromHours(2)), times.Recorded.Start);

        // The whole point of the omission rule: a third block repeating the second is noise.
        Assert.Null(times.Viewer);
    }

    [Fact]
    public void An_entry_recorded_elsewhere_gets_the_readers_own_reading_too()
    {
        var times = AppointmentTimes.For(
            new DateTime(2026, 8, 24, 11, 0, 0), new DateTime(2026, 8, 24, 12, 0, 0),
            isAllDay: false, "Europe/Zurich", "Europe/Zurich", Boston);

        Assert.NotNull(times);
        Assert.NotNull(times!.Viewer);

        // 11:00 in Zurich is 05:00 in Boston that day — the answer the reader actually needs.
        Assert.Equal(5, times.Viewer!.Start!.Value.Hour);
        Assert.Equal("America/New_York", times.Viewer.StartZoneId);
    }

    [Fact]
    public void The_end_keeps_its_own_zone()
    {
        // The flight: leaves Zurich 09:00, lands in Boston 11:30. Collapsing the endpoints into one zone —
        // which every earlier version of this did — makes it read as two and a half hours.
        var times = AppointmentTimes.For(
            new DateTime(2026, 8, 24, 9, 0, 0), new DateTime(2026, 8, 24, 11, 30, 0),
            isAllDay: false, "Europe/Zurich", "America/New_York", Zurich);

        Assert.NotNull(times);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 7, 0, 0, TimeSpan.Zero), times!.Utc.Start);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 15, 30, 0, TimeSpan.Zero), times.Utc.End);

        // Eight and a half hours in the air, which is the real number.
        Assert.Equal(TimeSpan.FromHours(8.5), times.Utc.End!.Value - times.Utc.Start!.Value);
        Assert.Equal("Europe/Zurich", times.Recorded.StartZoneId);
        Assert.Equal("America/New_York", times.Recorded.EndZoneId);

        // The reader is in Zurich, so the START matches their zone — but the END does not, and one differing
        // endpoint is enough to make the third block worth showing.
        Assert.NotNull(times.Viewer);
    }

    [Fact]
    public void A_floating_time_is_read_in_the_readers_zone_and_says_so_by_naming_none()
    {
        var times = AppointmentTimes.For(
            new DateTime(2026, 8, 24, 11, 0, 0), new DateTime(2026, 8, 24, 12, 0, 0),
            isAllDay: false, null, null, Zurich);

        Assert.NotNull(times);

        // Placed in the reader's zone — that IS what floating means — so the third block would repeat it.
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero), times!.Utc.Start);
        Assert.Null(times.Viewer);

        // And the recorded block names no zone, because the file names none. Null is the distinction the
        // client renders in words; substituting the reader's zone here would claim the entry says something
        // it does not.
        Assert.Null(times.Recorded.StartZoneId);
    }

    [Fact]
    public void An_all_day_entry_has_no_instant_to_place()
    {
        // A day is not a moment, and stamping midnight on it would invent one (ADR 0647). The pane shows its
        // single all-day row instead of three blocks of fiction.
        Assert.Null(AppointmentTimes.For(
            new DateTime(2026, 8, 24), new DateTime(2026, 8, 27), isAllDay: true, null, null, Zurich));
    }

    [Fact]
    public void An_entry_with_no_end_still_reads_in_three_zones()
    {
        var times = AppointmentTimes.For(
            new DateTime(2026, 8, 24, 11, 0, 0), null, isAllDay: false, "Europe/Zurich", null, Boston);

        Assert.NotNull(times);
        Assert.Null(times!.Utc.End);
        Assert.Null(times.Recorded.End);

        // A missing end must not decide the omission on its own: the start differs, so the block is shown.
        Assert.NotNull(times.Viewer);
        Assert.Null(times.Viewer!.End);
    }

    [Fact]
    public void An_unknown_zone_still_places_the_entry_and_still_reports_what_was_written()
    {
        var times = AppointmentTimes.For(
            new DateTime(2026, 8, 24, 11, 0, 0), null, isAllDay: false, "Mars/Olympus_Mons", null, Zurich);

        // Falls back to the reader's zone for the arithmetic rather than refusing — one exotic TZID must not
        // make a whole calendar unreadable — while the recorded block still says what the file said.
        Assert.NotNull(times);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero), times!.Utc.Start);
        Assert.Equal("Mars/Olympus_Mons", times.Recorded.StartZoneId);
    }

    [Fact]
    public void A_same_day_range_states_the_date_once_and_a_crossing_one_twice()
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");

        var sameDay = new ZonedRange(
            new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 24, 11, 30, 0, TimeSpan.Zero), "UTC", "UTC");
        Assert.Equal("24/08/2026 09:00 – 11:30", sameDay.Format(culture));
        Assert.True(sameDay.OneZone);

        // An overnight entry must repeat the date, or "23:00 – 01:30" claims a two-hour meeting that ended
        // before it began.
        var overnight = sameDay with { End = new DateTimeOffset(2026, 8, 25, 1, 30, 0, TimeSpan.Zero) };
        Assert.Equal("24/08/2026 09:00 – 25/08/2026 01:30", overnight.Format(culture));

        // No end: the start alone, never a dangling dash.
        Assert.Equal("24/08/2026 09:00", (sameDay with { End = null }).Format(culture));

        // Two zones is what makes a pane name them per endpoint instead of once.
        Assert.False((sameDay with { EndZoneId = "America/New_York" }).OneZone);
    }
}
