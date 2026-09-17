using SimplArchive.Domain.Documents;

namespace SimplArchive.UnitTests;

// The date+time pair as ONE instant, and a caller's calendar day as a UTC range (#1254).
//
// These are the boundaries, not the happy path. A day-range conversion that works at noon in January works
// everywhere except the four places it matters: midnight, the two DST transitions, and a document that never
// had a time at all. Each of those is a case where a plausible implementation is wrong in a way nobody notices
// until a document goes missing on exactly one day of the year.
public class DocumentInstantTests
{
    // Europe/Zurich by IANA id, which is what the app stores and what a browser reports. On a host without a
    // zone database these throw rather than silently answering — which is the honest outcome for a test: it
    // would be reporting on a machine that cannot do the thing under test. (#1138 is the fix for the image.)
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Fact]
    public void A_timed_document_is_an_instant()
    {
        var instant = DocumentInstant.Of(new DateOnly(2026, 7, 15), new TimeOnly(8, 0));

        Assert.NotNull(instant);
        Assert.Equal(TimeSpan.Zero, instant!.Value.Offset);
        Assert.Equal(new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc), instant.Value.UtcDateTime);
    }

    [Fact]
    public void A_date_only_document_is_NOT_an_instant()
    {
        // The case that must stay null. Giving it a notional midnight would make a document filed "on the
        // 15th" read as the 14th for anyone west of the server — inventing a bug in order to fix one that was
        // never there. Everything downstream branches on this null.
        Assert.Null(DocumentInstant.Of(new DateOnly(2026, 7, 15), null));
    }

    [Fact]
    public void A_summer_day_in_Zurich_is_the_UTC_range_two_hours_earlier()
    {
        var (from, to) = DocumentInstant.DayIn(new DateOnly(2026, 7, 16), Zurich);

        // 16 July 00:00 CEST == 15 July 22:00 UTC. So a search for "the 16th" must reach back into the 15th,
        // which is exactly the case a naive `documentDate == '2026-07-16'` gets wrong.
        Assert.Equal(new DateTime(2026, 7, 15, 22, 0, 0, DateTimeKind.Utc), from.UtcDateTime);
        Assert.Equal(new DateTime(2026, 7, 16, 22, 0, 0, DateTimeKind.Utc), to.UtcDateTime);
    }

    [Fact]
    public void A_winter_day_is_one_hour_earlier_not_two()
    {
        var (from, to) = DocumentInstant.DayIn(new DateOnly(2026, 1, 15), Zurich);

        // Asserted separately from the summer case on purpose: an implementation that hard-codes an offset
        // passes one of these and fails the other, and a single test at one time of year would not say which.
        Assert.Equal(new DateTime(2026, 1, 14, 23, 0, 0, DateTimeKind.Utc), from.UtcDateTime);
        Assert.Equal(new DateTime(2026, 1, 15, 23, 0, 0, DateTimeKind.Utc), to.UtcDateTime);
    }

    [Fact]
    public void The_day_DST_STARTS_is_twenty_three_hours_long()
    {
        // 29 March 2026: clocks go 02:00 → 03:00, so this day has 23 hours. The range must reflect that —
        // an implementation that computes the end as "start + 24h" is wrong here by an hour, and would drop
        // the last hour of the day from every search.
        var (from, to) = DocumentInstant.DayIn(new DateOnly(2026, 3, 29), Zurich);

        Assert.Equal(TimeSpan.FromHours(23), to - from);
    }

    [Fact]
    public void The_day_DST_ENDS_is_twenty_five_hours_long()
    {
        // 25 October 2026: clocks go 03:00 → 02:00, so this day has 25 hours — and 02:30 happens TWICE. The
        // range must cover both, which "start + 24h" would not.
        var (from, to) = DocumentInstant.DayIn(new DateOnly(2026, 10, 25), Zurich);

        Assert.Equal(TimeSpan.FromHours(25), to - from);
    }

    [Fact]
    public void A_day_whose_midnight_does_not_exist_still_yields_a_range()
    {
        // Not every zone changes its clocks at 02:00. America/Santiago moves them at midnight, so on
        // 6 September 2026 the wall clock 00:00 never happens — IsInvalidTime says so.
        //
        // GetUtcOffset does NOT throw for such a time; it returns the pre-transition offset, and the instant
        // that yields is already the first real one after the gap. Worth recording because the obvious guard
        // ("if invalid, add an hour") was written here first and removing it changed no result — it is dead
        // where the gap is an hour, and WRONG where it is not, as on Lord Howe Island whose clocks move
        // thirty minutes.
        var santiago = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");

        var (from, to) = DocumentInstant.DayIn(new DateOnly(2026, 9, 6), santiago);

        // ASSERTS THE INSTANT, not merely that the range is non-empty. The weaker assertion was the first
        // version of this test and it was worthless: it held whether or not the skipped hour was handled at
        // all, so it could not tell a correct implementation from one that had simply not thought about it.
        //
        // 00:00 does not exist, so the day begins at the first instant that does — 01:00 local, offset -03:00,
        // which is 04:00 UTC. The day is 23 hours long because an hour of it was skipped.
        Assert.Equal(new DateTime(2026, 9, 6, 4, 0, 0, DateTimeKind.Utc), from.UtcDateTime);
        Assert.Equal(TimeSpan.FromHours(23), to - from);
    }

    [Fact]
    public void Consecutive_days_meet_exactly_with_no_gap_and_no_overlap()
    {
        // The half-open property, asserted rather than assumed: an inclusive upper bound either double-counts
        // midnight (a document found on two days) or drops the final second (a document found on none).
        var first = DocumentInstant.DayIn(new DateOnly(2026, 7, 16), Zurich);
        var second = DocumentInstant.DayIn(new DateOnly(2026, 7, 17), Zurich);

        Assert.Equal(first.To, second.From);
    }
}
