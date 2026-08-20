using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// Which days a calendar entry occupies (#660). Written once and tested once BECAUSE both clients ask it: the
// web grid and the desktop grid are one surface (ADR 0511), and a month view whose two halves disagree about a
// Tuesday is worse than one that is wrong in the same way twice.
//
// Every case here is an off-by-one. None of them crashes, none is visible without counting, and the sister
// project shipped the first one and corrected it afterwards (SimplCalCon ADR 0072) — which is the argument for
// pinning them at the arithmetic rather than through a rendered grid.
public class AppointmentCoverageTests
{
    private static readonly DateOnly First = new(2026, 9, 15);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Fact]
    public void A_timed_entry_covers_every_day_from_its_start_to_its_end()
    {
        var last = AppointmentCoverage.LastDay(First, allDay: false, endDay: null, ends: At(First.AddDays(2), 17));

        Assert.Equal(First.AddDays(2), last);

        // The middle day is the one a start-day-only implementation loses, and the one a reader most needs:
        // "is the conference still on?" is asked on day two, not day one.
        Assert.True(Covers(First.AddDays(1), ends: At(First.AddDays(2), 17)));
        Assert.False(Covers(First.AddDays(-1), ends: At(First.AddDays(2), 17)));
        Assert.False(Covers(First.AddDays(3), ends: At(First.AddDays(2), 17)));
    }

    [Fact]
    public void An_entry_ending_at_exactly_midnight_stops_the_previous_evening()
    {
        // 22:00 → 00:00 is one evening, not two days. Counting the midnight would put a chip on a day the entry
        // is over, which is the same lie as losing one, told the other way round.
        Assert.Equal(First, AppointmentCoverage.LastDay(First, false, null, At(First.AddDays(1), 0)));
        Assert.False(Covers(First.AddDays(1), ends: At(First.AddDays(1), 0)));

        // One minute past midnight and it genuinely does run into the next day.
        Assert.Equal(First.AddDays(1), AppointmentCoverage.LastDay(First, false, null, At(First.AddDays(1), 0, 1)));
        Assert.True(Covers(First.AddDays(1), ends: At(First.AddDays(1), 0, 1)));
    }

    [Fact]
    public void An_all_day_entrys_DTEND_is_the_day_it_stops_not_its_last_day()
    {
        // iCalendar's DTEND is exclusive: a two-day festival starting on the 15th carries DTEND of the 17th.
        // Reading it as the last day would draw a third day that does not exist.
        var last = AppointmentCoverage.LastDay(First, allDay: true, endDay: First.AddDays(2), ends: null);

        Assert.Equal(First.AddDays(1), last);
        Assert.True(CoversAllDay(First.AddDays(1), First.AddDays(2)));
        Assert.False(CoversAllDay(First.AddDays(2), First.AddDays(2)));

        // A single all-day entry has DTEND on the following day, and occupies exactly one cell.
        Assert.Equal(First, AppointmentCoverage.LastDay(First, true, First.AddDays(1), null));
    }

    [Fact]
    public void A_missing_zero_or_backwards_duration_covers_the_start_day_alone()
    {
        // Bad data should draw the entry ONCE, never make it vanish: an entry nobody can see is
        // indistinguishable from one that was never filed, and that is the harder bug to notice.
        Assert.Equal(First, AppointmentCoverage.LastDay(First, false, null, ends: null));
        Assert.Equal(First, AppointmentCoverage.LastDay(First, false, null, At(First, 9)));
        Assert.Equal(First, AppointmentCoverage.LastDay(First, false, null, At(First.AddDays(-3), 9)));
        Assert.Equal(First, AppointmentCoverage.LastDay(First, true, First.AddDays(-3), null));

        Assert.True(Covers(First, ends: At(First.AddDays(-3), 9)));
        Assert.False(Covers(First.AddDays(-1), ends: At(First.AddDays(-3), 9)));
    }

    [Fact]
    public void An_undated_entry_covers_no_day_at_all()
    {
        // It belongs to the list, which sorts undated entries last; a grid has nowhere to put it, and inventing
        // a day for it is the inference ADR 0647 refuses.
        Assert.Null(AppointmentCoverage.LastDay(null, false, null, At(First, 9)));
        Assert.False(AppointmentCoverage.CoversDay(First, null, false, null, At(First, 9)));
        Assert.False(AppointmentCoverage.ContinuesOn(First, null, false, null, At(First, 9)));
    }

    [Fact]
    public void Only_a_day_after_the_first_is_a_continuation()
    {
        var ends = At(First.AddDays(2), 17);

        // The distinction the cell renders: the first day shows a time, the rest show the mark. Saying "…" on
        // the first day would hide when it starts; saying "09:00" on the third would claim it began that morning.
        Assert.False(AppointmentCoverage.ContinuesOn(First, First, false, null, ends));
        Assert.True(AppointmentCoverage.ContinuesOn(First.AddDays(1), First, false, null, ends));
        Assert.True(AppointmentCoverage.ContinuesOn(First.AddDays(2), First, false, null, ends));

        // A day it does not cover is not a continuation either — the two questions are asked together.
        Assert.False(AppointmentCoverage.ContinuesOn(First.AddDays(3), First, false, null, ends));
    }

    private static bool Covers(DateOnly day, DateTimeOffset ends) =>
        AppointmentCoverage.CoversDay(day, First, allDay: false, endDay: null, ends: ends);

    private static bool CoversAllDay(DateOnly day, DateOnly stops) =>
        AppointmentCoverage.CoversDay(day, First, allDay: true, endDay: stops, ends: null);
}
