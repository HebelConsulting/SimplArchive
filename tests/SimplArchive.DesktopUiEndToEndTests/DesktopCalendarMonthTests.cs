using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Presentation;

namespace SimplArchive.UiEndToEndTests;

// The desktop month grid (#660), the twin of the web one — the pair is one surface (ADR 0511).
//
// Pure view-model tests: no server, no window. That is deliberate, because every bug this grid actually
// produced was in the bucketing and the state, not in the markup — an infinite Refresh() recursion, a grid
// that did not fill its pane, two converters that do not exist. The first was found by rendering headlessly
// and the last two by reading the XAML, so what is left to guard is the arithmetic, and it is worth being
// able to guard it in milliseconds.
public class DesktopCalendarMonthTests
{
    private static AppointmentRowViewModel Row(
        string title, DateOnly day, TimeOnly? at = null, bool allDay = false, string? repeats = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CollectionColor = "#8a8a8a",
            CollectionName = "Personal / My Calendar",
            AllDay = allDay,
            IndexedDay = allDay ? day : null,
            Repeats = repeats,
            Title = title,
            Start = Moment(day, at),
            // An hour long, as a real entry is: with no End, TimeRange collapses to the single start time and
            // the range/start distinction this file asserts would be untestable.
            End = at is { } time ? Moment(day, time.AddHours(1)) : null,
            Links = new Dictionary<string, string>(),
        };

    private static DateTimeOffset? Moment(DateOnly day, TimeOnly? at) =>
        at is { } time ? new DateTimeOffset(day.ToDateTime(time), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(time))) : null;

    private static CalendarTabViewModel OnMonth(DateOnly month, params AppointmentRowViewModel[] rows)
    {
        var vm = new CalendarTabViewModel { Month = month };
        foreach (var row in rows)
        {
            vm.Appointments.Add(row);
        }

        return vm;
    }

    [Fact]
    public void The_grid_is_always_six_whole_weeks_starting_on_the_cultures_first_day()
    {
        var vm = OnMonth(new DateOnly(2026, 9, 1));

        Assert.Equal(42, vm.MonthDays.Count);
        Assert.Equal(7, vm.WeekdayHeadings.Count);

        // A fixed count is what keeps the grid from changing height as the user pages, which otherwise moves
        // the cell out from under the cursor mid-click. February 2026 starts on a Sunday and is 28 days long —
        // the shortest possible month, and the one a "just enough weeks" grid would render as five.
        Assert.Equal(42, OnMonth(new DateOnly(2026, 2, 1)).MonthDays.Count);

        var first = vm.MonthDays[0].Day;
        Assert.Equal(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, first.DayOfWeek);
        Assert.True(first <= new DateOnly(2026, 9, 1), "the grid starts after the first of the month — the lead-in is missing.");
    }

    [Fact]
    public void The_lead_in_and_lead_out_days_are_marked_as_outside_the_month()
    {
        var vm = OnMonth(new DateOnly(2026, 9, 1));

        // Context, not content: an entry on 31 August must be visibly not-September, or the reader miscounts
        // the month they are looking at.
        Assert.All(vm.MonthDays, d => Assert.Equal(d.Day.Month == 9, d.InMonth));
        Assert.Contains(vm.MonthDays, d => !d.InMonth);
        Assert.Equal(30, vm.MonthDays.Count(d => d.InMonth));
    }

    [Fact]
    public void A_day_cell_holds_its_own_entries_all_day_first_and_counts_what_did_not_fit()
    {
        var day = new DateOnly(2026, 9, 15);
        var vm = OnMonth(new DateOnly(2026, 9, 1),
            Row("Late set", day, new TimeOnly(22, 0)),
            Row("Soundcheck", day, new TimeOnly(9, 0)),
            Row("Matinee", day, new TimeOnly(14, 0)),
            Row("Festival", day, allDay: true),
            Row("Elsewhere", day.AddDays(1), new TimeOnly(9, 0)));

        var cell = vm.MonthDays.Single(d => d.Day == day);

        // All-day first — an entry covering the whole day is context for the rest — then by time.
        Assert.Equal(["Festival", "Soundcheck"], cell.Entries.Select(e => e.Title));

        // The rest become a count, not a taller cell: a cell that grows to fit reflows the whole grid.
        Assert.Equal(2, cell.Hidden);
        Assert.True(cell.HasHidden);

        Assert.Equal(["Elsewhere"], vm.MonthDays.Single(d => d.Day == day.AddDays(1)).Entries.Select(e => e.Title));
        Assert.All(vm.MonthDays.Where(d => d.Day != day && d.Day != day.AddDays(1)), d => Assert.Empty(d.Entries));
    }

    [Fact]
    public void Paging_moves_the_month_and_Today_comes_back()
    {
        var vm = OnMonth(new DateOnly(2026, 9, 1));
        var september = vm.MonthName;

        vm.NextMonthCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 10, 1), vm.Month);
        Assert.NotEqual(september, vm.MonthName);
        Assert.Equal(42, vm.MonthDays.Count);

        vm.PreviousMonthCommand.Execute(null);
        Assert.Equal(september, vm.MonthName);

        vm.GoTodayCommand.Execute(null);
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(today.AddDays(1 - today.Day), vm.Month);
        Assert.Contains(vm.MonthDays, d => d.IsToday && d.InMonth);
    }

    // The decision (#660): "+N more" sends the reader to the LIST narrowed to that day, rather than growing
    // the cell. Both halves matter — switching without filtering dumps them in the full list, and filtering
    // without switching leaves them staring at the same capped cell.
    [Fact]
    public void Opening_a_day_switches_to_the_list_and_narrows_it_to_that_day()
    {
        var day = new DateOnly(2026, 9, 15);
        var vm = OnMonth(new DateOnly(2026, 9, 1),
            Row("Soundcheck", day, new TimeOnly(9, 0)),
            Row("Matinee", day, new TimeOnly(14, 0)),
            Row("Late set", day, new TimeOnly(22, 0)),
            Row("Another day", day.AddDays(2), new TimeOnly(9, 0)));

        vm.ShowDayCommand.Execute(vm.MonthDays.Single(d => d.Day == day));

        Assert.False(vm.IsMonthView);
        Assert.Equal(day, vm.DayFilter);
        Assert.Equal(3, vm.VisibleAppointments.Count());
        Assert.DoesNotContain(vm.VisibleAppointments, a => a.Title == "Another day");

        // Cleared in place, without leaving the list: the reader asked for one day, not for a different view.
        vm.ClearDayFilterCommand.Execute(null);
        Assert.False(vm.IsMonthView);
        Assert.Equal(4, vm.VisibleAppointments.Count());
    }

    // A month narrowed to one day is an EMPTY month, so going back to the grid must drop the filter. Leaving
    // it on would render 41 blank cells and read as data loss.
    [Fact]
    public void Returning_to_the_grid_drops_the_day_filter()
    {
        var day = new DateOnly(2026, 9, 15);
        var vm = OnMonth(new DateOnly(2026, 9, 1),
            Row("Soundcheck", day, new TimeOnly(9, 0)),
            Row("Another day", day.AddDays(2), new TimeOnly(9, 0)));

        vm.ShowDayCommand.Execute(vm.MonthDays.Single(d => d.Day == day));
        vm.ShowMonthCommand.Execute(null);

        Assert.True(vm.IsMonthView);
        Assert.Null(vm.DayFilter);
        Assert.Equal(2, vm.MonthDays.Sum(d => d.Entries.Count));
    }

    // A day cell is about 85 px wide. Binding the full range there trimmed the title to nothing, so every entry
    // read as a time and an ellipsis — identifying no more than the ISO-date names it sat beside. Caught by
    // rendering it, not by any test, which is why the distinction is now pinned.
    [Fact]
    public void A_cell_shows_the_start_alone_while_the_row_keeps_the_range()
    {
        var day = new DateOnly(2026, 9, 15);
        var timed = Row("Soundcheck", day, new TimeOnly(9, 0));
        var allDay = Row("Festival", day, allDay: true);

        Assert.Equal("09:00", timed.StartTimeShort);
        Assert.Contains("–", timed.TimeRange, StringComparison.Ordinal);
        Assert.DoesNotContain("–", timed.StartTimeShort, StringComparison.Ordinal);

        // Nothing for an all-day entry: it has no time, and none should be invented (ADR 0647).
        Assert.Equal(string.Empty, allDay.StartTimeShort);
    }

    // The text filter is the one narrowing that DOES apply to the grid: it answers "where is this act playing",
    // which is a question about the month. The day filter answers a question about a day and does not.
    [Fact]
    public void The_text_filter_narrows_the_grid_too()
    {
        var day = new DateOnly(2026, 9, 15);
        var vm = OnMonth(new DateOnly(2026, 9, 1),
            Row("Soundcheck", day, new TimeOnly(9, 0)),
            Row("Matinee", day.AddDays(3), new TimeOnly(14, 0)));

        vm.Filter = "sound";

        Assert.Equal(1, vm.MonthDays.Sum(d => d.Entries.Count));
        Assert.Equal("Soundcheck", vm.MonthDays.Single(d => d.Entries.Count > 0).Entries[0].Title);
    }

    // Multi-day coverage. A chip placed only on its start day makes a three-day conference appear on day one
    // and then vanish — which reads as "nothing is happening" on exactly the days something is. The sister
    // project shipped that and had to correct it (SimplCalCon ADR 0072); this is that correction, arrived at
    // before shipping rather than after.
    [Fact]
    public void A_multi_day_entry_is_on_show_every_day_it_covers()
    {
        var first = new DateOnly(2026, 9, 15);
        var conference = Timed("Conference", first, new TimeOnly(9, 0), first.AddDays(2), new TimeOnly(17, 0));

        Assert.Equal(first.AddDays(2), conference.LastDay);
        Assert.True(conference.CoversDay(first));
        Assert.True(conference.CoversDay(first.AddDays(1)));
        Assert.True(conference.CoversDay(first.AddDays(2)));
        Assert.False(conference.CoversDay(first.AddDays(-1)));
        Assert.False(conference.CoversDay(first.AddDays(3)));

        // Only the middle and last days are continuations — the first one starts, and says so with its time.
        Assert.False(conference.ContinuesOn(first));
        Assert.True(conference.ContinuesOn(first.AddDays(1)));

        var vm = OnMonth(new DateOnly(2026, 9, 1), conference);
        Assert.Equal(3, vm.MonthDays.Count(d => d.Entries.Count > 0));

        // The lead text is what distinguishes the two: a time where it starts, the mark where it carries on.
        // Repeating "09:00" on day three would state it began that morning.
        var cells = vm.MonthDays.Where(d => d.Entries.Count > 0).ToList();
        Assert.Equal("09:00", cells[0].Entries[0].LeadText);
        Assert.Equal(AppointmentRowViewModel.ContinuationMark, cells[1].Entries[0].LeadText);
        Assert.Equal(AppointmentRowViewModel.ContinuationMark, cells[2].Entries[0].LeadText);
    }

    // The end is EXCLUSIVE at midnight, in both shapes. Getting this wrong is not a crash but an off-by-one
    // day, which is invisible until someone counts.
    [Fact]
    public void An_end_at_midnight_does_not_add_a_day()
    {
        var first = new DateOnly(2026, 9, 15);

        // A timed entry ending at exactly 00:00 stops the previous evening; it does not occupy the 16th.
        var overnight = Timed("Late set", first, new TimeOnly(22, 0), first.AddDays(1), new TimeOnly(0, 0));
        Assert.Equal(first, overnight.LastDay);
        Assert.False(overnight.CoversDay(first.AddDays(1)));

        // One minute later and it genuinely does run into the next day.
        var justOver = Timed("Later set", first, new TimeOnly(22, 0), first.AddDays(1), new TimeOnly(0, 1));
        Assert.Equal(first.AddDays(1), justOver.LastDay);

        // An all-day entry's DTEND is the day it STOPS, so a two-day festival ends on DTEND minus one.
        var festival = AllDayRun("Festival", first, first.AddDays(2));
        Assert.Equal(first.AddDays(1), festival.LastDay);
        Assert.True(festival.CoversDay(first.AddDays(1)));
        Assert.False(festival.CoversDay(first.AddDays(2)));
    }

    // Bad data should draw the entry once, never make it disappear: an entry nobody can see is indistinguishable
    // from one that was never filed.
    [Fact]
    public void A_missing_or_backwards_duration_covers_the_start_day_alone()
    {
        var first = new DateOnly(2026, 9, 15);

        var noEnd = Row("Open ended", first, new TimeOnly(9, 0));
        Assert.Equal(first, noEnd.LastDay);
        Assert.True(noEnd.CoversDay(first));

        var backwards = Timed("Backwards", first, new TimeOnly(9, 0), first.AddDays(-3), new TimeOnly(9, 0));
        Assert.Equal(first, backwards.LastDay);
        Assert.True(backwards.CoversDay(first));
        Assert.False(backwards.CoversDay(first.AddDays(-1)));

        // An undated entry covers no day at all — it belongs to the list, which sorts it last.
        var undated = Row("Someday", first);
        Assert.Null(undated.Day);
        Assert.Null(undated.LastDay);
        Assert.False(undated.CoversDay(first));
    }

    // Clicking "+N more" on a middle day must hand back what that cell showed. Matching on the start day would
    // give a shorter list than the grid did, for the one entry the user is most likely asking about.
    [Fact]
    public void The_day_filter_keeps_an_entry_that_merely_runs_through_that_day()
    {
        var first = new DateOnly(2026, 9, 15);
        var vm = OnMonth(new DateOnly(2026, 9, 1),
            Timed("Conference", first, new TimeOnly(9, 0), first.AddDays(2), new TimeOnly(17, 0)),
            Row("Unrelated", first.AddDays(1), new TimeOnly(9, 0)));

        vm.ShowDayCommand.Execute(vm.MonthDays.Single(d => d.Day == first.AddDays(1)));

        Assert.Equal(["Conference", "Unrelated"], vm.VisibleAppointments.Select(a => a.Title).Order());
    }

    // The rule is never expanded, so a weekly rehearsal is drawn once. A limitation is fine; a SILENT one is a
    // grid quietly asserting the other three weeks are free, which is a stronger claim than the data supports.
    [Fact]
    public void A_repeating_entry_carries_a_marker_and_a_one_off_does_not()
    {
        var day = new DateOnly(2026, 9, 15);
        var weekly = Row("Rehearsal", day, new TimeOnly(19, 0), repeats: "FREQ=WEEKLY;BYDAY=TU");
        var once = Row("Soundcheck", day, new TimeOnly(9, 0));

        Assert.True(weekly.Recurring);
        Assert.Equal(AppointmentCoverage.RepeatMark, weekly.RepeatMark);

        // The negative half is the one that gives the marker meaning: a mark on every row says nothing.
        Assert.False(once.Recurring);
        Assert.Equal(string.Empty, once.RepeatMark);

        // And it survives into the cell, which is the thing the grid actually binds.
        var vm = OnMonth(new DateOnly(2026, 9, 1), weekly, once);
        var cell = vm.MonthDays.Single(d => d.Day == day);
        Assert.Equal([false, true], cell.Entries.Select(e => e.Recurring));
    }

    /// <summary>A timed entry spanning two given moments — the shape a conference or an overnight set has.</summary>
    private static AppointmentRowViewModel Timed(
        string title, DateOnly from, TimeOnly at, DateOnly until, TimeOnly then)
    {
        var row = Row(title, from, at);
        row.End = Moment(until, then);
        return row;
    }

    /// <summary>An all-day run, given iCalendar's EXCLUSIVE DTEND — the day the entry stops, not its last day.</summary>
    private static AppointmentRowViewModel AllDayRun(string title, DateOnly from, DateOnly stops)
    {
        var row = Row(title, from, allDay: true);
        return new AppointmentRowViewModel
        {
            Id = row.Id,
            CollectionColor = row.CollectionColor,
            CollectionName = row.CollectionName,
            AllDay = true,
            IndexedDay = from,
            IndexedEndDay = stops,
            Title = title,
            Links = row.Links,
        };
    }
}
