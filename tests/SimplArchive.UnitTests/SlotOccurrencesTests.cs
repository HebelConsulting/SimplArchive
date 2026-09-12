using SimplArchive.Domain.Booking;

namespace SimplArchive.UnitTests;

// Expanding a repeating slot into the occurrences that fall in a range.
//
// One expander for windows, claims and blocks: the question asked of the three is identical — which
// occurrences of this fall between these two instants — and only the meaning of an occurrence differs. Three
// copies is how one of them comes to disagree with the others about whether an EXDATE counts.
//
// Reported from use: an availability window entered as "08:00-22:00, 12th to 30th" was stored as one unbroken
// span INCLUDING every night between, so a booking at 19:00-23:30 fell inside it and was allowed. Daily hours
// are a repeat, and until now a repeating window was refused outright.
public class SlotOccurrencesTests
{
    private sealed record Slot(
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc,
        string? RecurrenceRule = null,
        string? ExceptionDates = null) : IRecurringSlot;

    private static DateTimeOffset At(int day, int hour) => new(2026, 9, day, hour, 0, 0, TimeSpan.Zero);

    // A slot that does not repeat yields itself, so every caller treats the two cases alike instead of
    // branching on "does this repeat" at each site.
    [Fact]
    public void A_one_off_slot_yields_itself()
    {
        var slot = new Slot(At(10, 9), At(10, 12));

        Assert.Equal([new SlotOccurrence(At(10, 9), At(10, 12))], SlotOccurrences.Between(slot, At(10, 0), At(11, 0)));
    }

    [Fact]
    public void A_one_off_slot_outside_the_range_yields_nothing() =>
        Assert.Empty(SlotOccurrences.Between(new Slot(At(10, 9), At(10, 12)), At(20, 0), At(21, 0)));

    // THE REPORTED CASE. Daily 09:00-12:00 is not a continuous span: the 17th's occurrence ends at 12:00, so a
    // booking running to 13:00 is not covered — where one unbroken 12th-to-30th span would have swallowed it.
    [Fact]
    public void A_daily_rule_does_not_cover_the_gaps_between_its_occurrences()
    {
        var daily = new Slot(At(12, 9), At(12, 12), "FREQ=DAILY;UNTIL=20260930T220000Z");

        Assert.True(SlotOccurrences.AnyCovers(daily, At(17, 10), At(17, 11)));
        Assert.False(SlotOccurrences.AnyCovers(daily, At(17, 11), At(17, 13)));
        Assert.False(SlotOccurrences.AnyCovers(daily, At(17, 19), At(17, 23)));
    }

    // Coverage by a SINGLE occurrence, unchanged by repetition (ADR 0780): two consecutive days do not add up
    // to one offer for the night between them.
    [Fact]
    public void Two_occurrences_do_not_add_up_to_one_offer()
    {
        var daily = new Slot(At(12, 9), At(12, 12), "FREQ=DAILY;COUNT=10");

        Assert.False(SlotOccurrences.AnyCovers(daily, At(17, 10), At(18, 11)));
    }

    // An occurrence that STARTS before the range and runs into it must still be found — the case that decides
    // whether a night-spanning window covers an early-morning booking. It is why the search starts one
    // duration early.
    [Fact]
    public void An_occurrence_starting_before_the_range_is_found()
    {
        var nightly = new Slot(At(12, 22), At(13, 2), "FREQ=DAILY;COUNT=10");

        Assert.True(SlotOccurrences.AnyCovers(nightly, At(18, 0), At(18, 1)));
    }

    // EXDATE is how iCalendar cancels ONE occurrence, and any CalDAV client writes it natively. Ignoring it
    // would leave the stored claim and the displayed one disagreeing about a single day.
    [Fact]
    public void A_cancelled_occurrence_is_skipped()
    {
        var rule = "FREQ=DAILY;COUNT=10";
        var withException = new Slot(At(12, 9), At(12, 12), rule, At(17, 9).ToString("O"));

        Assert.True(SlotOccurrences.AnyCovers(new Slot(At(12, 9), At(12, 12), rule), At(17, 10), At(17, 11)));
        Assert.False(SlotOccurrences.AnyCovers(withException, At(17, 10), At(17, 11)));
        Assert.True(SlotOccurrences.AnyCovers(withException, At(18, 10), At(18, 11)));
    }

    // A weekly rule repeats on its own weekday and no other.
    [Fact]
    public void A_weekly_rule_repeats_on_its_own_weekday()
    {
        var weekly = new Slot(At(14, 9), At(14, 12), "FREQ=WEEKLY;COUNT=4");   // 14 Sep 2026 is a Monday

        Assert.True(SlotOccurrences.AnyCovers(weekly, At(21, 10), At(21, 11)));
        Assert.False(SlotOccurrences.AnyCovers(weekly, At(22, 10), At(22, 11)));
    }

    // An endless rule is answerable WITHOUT a horizon, because expansion only ever crosses the range asked
    // about. That is what lets an offer repeat forever while a claim must be bounded.
    [Fact]
    public void An_endless_rule_is_expanded_only_across_the_range_asked_about()
    {
        var endless = new Slot(At(14, 9), At(14, 12), "FREQ=WEEKLY");

        Assert.True(SlotOccurrences.AnyCovers(endless, new DateTimeOffset(2031, 9, 15, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2031, 9, 15, 11, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(null, true)]                            // does not repeat
    [InlineData("FREQ=WEEKLY;COUNT=12", true)]
    [InlineData("FREQ=DAILY;UNTIL=20260930T220000Z", true)]
    [InlineData("FREQ=WEEKLY", false)]                  // endless
    public void Boundedness_is_what_a_claim_is_held_to(string? rule, bool bounded) =>
        Assert.Equal(bounded, SlotOccurrences.IsBounded(new Slot(At(14, 9), At(14, 12), rule)));
}
