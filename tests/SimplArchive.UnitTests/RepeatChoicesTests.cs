using SimplArchive.Domain.Booking;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The repeats an editor offers, shared so the two clients cannot offer different ones (#1133).
//
// The list is deliberately short — RRULE can express "the last Thursday of every second month", and an editor
// that tried to offer that becomes a rule builder. What it must cover is what people actually publish: daily
// hours, WORKING DAYS, a weekly slot.
public class RepeatChoicesTests
{
    // Every offered rule has to be one the expander actually understands. A picker that writes a rule the
    // server cannot expand would be an affordance that fails on save.
    [Fact]
    public void Every_offered_rule_expands()
    {
        var monday = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

        foreach (var choice in RepeatChoices.All.Where(c => c.Rule is not null))
        {
            var slot = new Repeating(monday, monday.AddHours(1), choice.Rule);
            // Three years, so the window is long enough for the slowest frequency on the list: one year is
            // exactly ONE occurrence of FREQ=YEARLY, which would read as "does not repeat".
            var occurrences = SlotOccurrences.Between(slot, monday, monday.AddYears(3));

            Assert.True(occurrences.Count > 1, $"'{choice.Key}' ({choice.Rule}) produced no repeat at all");
        }
    }

    // THE ONE ASKED FOR: Monday to Friday, which is how a room's opening hours and a school timetable are
    // actually written. Expressed as BYDAY rather than as a daily series with weekends cancelled, so reading
    // it back says "every weekday" instead of showing a rule riddled with exceptions.
    [Fact]
    public void Weekdays_repeats_monday_to_friday_and_skips_the_weekend()
    {
        var monday = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        var slot = new Repeating(monday, monday.AddHours(1), RepeatChoices.RuleFor(RepeatChoices.Weekdays));

        var days = SlotOccurrences.Between(slot, monday, monday.AddDays(7))
            .Select(o => o.StartsAtUtc.DayOfWeek)
            .ToList();

        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            days);
    }

    [Theory]
    [InlineData(null, RepeatChoices.None)]
    [InlineData("FREQ=DAILY", RepeatChoices.Daily)]
    [InlineData("FREQ=WEEKLY", RepeatChoices.Weekly)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", RepeatChoices.Weekdays)]
    // Order and case must not matter: a client or a server that reorders the parts must not make the picker
    // fall through to "richer than the list" and go read-only on a rule it offers itself.
    [InlineData("BYDAY=FR,TH,WE,TU,MO;FREQ=WEEKLY", RepeatChoices.Weekdays)]
    [InlineData("freq=weekly;byday=mo,tu,we,th,fr", RepeatChoices.Weekdays)]
    public void A_known_rule_is_recognised(string? rule, string expected) =>
        Assert.Equal(expected, RepeatChoices.KeyFor(rule));

    // Null means "richer than this list". The editor must then show the rule read-only rather than offer the
    // nearest button, which on the next save would turn "every second Tuesday" into "every Tuesday".
    [Theory]
    [InlineData("FREQ=WEEKLY;INTERVAL=2")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1TH")]
    public void A_richer_rule_is_not_claimed_by_the_list(string rule) =>
        Assert.Null(RepeatChoices.KeyFor(rule));

    // An UNTIL must not stop the picker recognising its own rule — it is the same repeat with an end.
    [Fact]
    public void An_until_does_not_hide_the_repeat() =>
        Assert.Equal(
            RepeatChoices.Weekdays,
            RepeatChoices.KeyFor(RepeatChoices.WithoutUntil(
                RepeatChoices.WithUntil(RepeatChoices.RuleFor(RepeatChoices.Weekdays), new DateOnly(2026, 9, 30)))));

    // The END of the day: "until 30 September" includes the 30th, and an UNTIL at midnight would drop that
    // day's occurrence — an off-by-one nobody notices until the last day of a series is missing.
    [Fact]
    public void Until_includes_the_day_it_names()
    {
        var start = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        var rule = RepeatChoices.WithUntil(RepeatChoices.RuleFor(RepeatChoices.Daily), new DateOnly(2026, 9, 30));
        var slot = new Repeating(start, start.AddHours(1), rule);

        var lastDay = SlotOccurrences.Between(slot, start, start.AddDays(10)).Last().StartsAtUtc;

        Assert.Equal(new DateOnly(2026, 9, 30), DateOnly.FromDateTime(lastDay.UtcDateTime));
    }

    [Fact]
    public void The_until_survives_a_round_trip() =>
        Assert.Equal(
            new DateOnly(2026, 9, 30),
            RepeatChoices.UntilOf(RepeatChoices.WithUntil("FREQ=DAILY", new DateOnly(2026, 9, 30))));

    private sealed record Repeating(
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc,
        string? RecurrenceRule = null,
        string? ExceptionDates = null) : IRecurringSlot;
}
