using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopUiEndToEndTests;

// The repeat is editable for the repeats the shared list offers, and states anything richer (#1133).
//
// Recurrence used to be shown and never edited, on the grounds that changing a series properly means choosing
// between this occurrence / this and following / all events. That reasoning holds for EDITING an existing
// series and is answered elsewhere; it never applied to choosing a repeat in the first place, which is what a
// room's opening hours are.
public class AppointmentRepeatTests
{
    // The one asked for by name: Monday to Friday, offered as its own choice rather than left to be built out
    // of a daily rule and two cancellations.
    [Fact]
    public void Weekdays_is_offered()
    {
        var form = new AppointmentEditViewModel();

        Assert.Contains(form.RepeatOptions, option => option.Key == RepeatChoices.Weekdays);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", RepeatChoices.RuleFor(RepeatChoices.Weekdays));
    }

    // Every option carries a LABEL, not a key: a picker showing "weekdays" in a German session would be the
    // shared vocabulary leaking into the surface it exists to keep consistent.
    [Fact]
    public void Every_option_is_worded_for_a_person() =>
        Assert.All(form().RepeatOptions, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Label));
            Assert.NotEqual(option.Key, option.Label);
        });

    [Fact]
    public void Choosing_a_repeat_and_an_end_writes_the_rule()
    {
        var f = form();
        f.RepeatKey = RepeatChoices.Weekdays;
        f.RepeatUntil = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        f.WriteRepeatIntoRule();

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;UNTIL=20260930T235959Z", f.RecurrenceRule);
    }

    [Fact]
    public void A_stored_rule_is_read_back_into_the_picker()
    {
        var f = form();
        f.RecurrenceRule = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;UNTIL=20260930T235959Z";

        f.ReadRepeatFromRule();

        Assert.Equal(RepeatChoices.Weekdays, f.RepeatKey);
        Assert.Equal(new DateTime(2026, 9, 30), f.RepeatUntil);
    }

    [Fact]
    public void Choosing_no_repeat_clears_the_rule()
    {
        var f = form();
        f.RecurrenceRule = "FREQ=DAILY";
        f.RepeatKey = RepeatChoices.None;

        f.WriteRepeatIntoRule();

        Assert.Null(f.RecurrenceRule);
    }

    // A rule the list does not cover must survive a save UNTOUCHED. Offering the nearest button would turn
    // "every second Tuesday" into "every Tuesday" the next time anybody opened the entry to fix a typo.
    [Fact]
    public void A_richer_rule_is_stated_rather_than_replaced()
    {
        var f = form();
        f.RecurrenceRule = "FREQ=WEEKLY;INTERVAL=2";
        f.ReadRepeatFromRule();

        Assert.True(f.RepeatIsRicherThanOffered);

        f.WriteRepeatIntoRule();

        Assert.Equal("FREQ=WEEKLY;INTERVAL=2", f.RecurrenceRule);
    }

    // ...and an ordinary rule is not mistaken for a richer one, or the picker would hide itself on the very
    // repeats it offers.
    [Fact]
    public void An_offered_rule_is_not_treated_as_richer()
    {
        var f = form();
        f.RecurrenceRule = "FREQ=DAILY;UNTIL=20260930T235959Z";
        f.ReadRepeatFromRule();

        Assert.False(f.RepeatIsRicherThanOffered);
        Assert.Equal(RepeatChoices.Daily, f.RepeatKey);
    }

    private static AppointmentEditViewModel form() => new();
}
