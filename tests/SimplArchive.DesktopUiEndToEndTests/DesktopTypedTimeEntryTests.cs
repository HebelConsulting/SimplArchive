using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The typed-not-picked principle in the last two desktop dialogs that still spun (#1057, after #1056 did the
// booking dialog and the web): a person setting an appointment or a reminder knows the time, so the keyboard
// is the way in — `0950`, `9:50`, `09:50` all land, an empty box means no time, and a value that is not a
// time is refused inline instead of being silently dropped.
//
// Pure view-model: what the dialogs bind is the Entry string, and these drive the same commit path the Save
// button does.
public class DesktopTypedTimeEntryTests
{
    [Theory]
    [InlineData("0950")]
    [InlineData("9:50")]
    [InlineData("09:50")]
    public void An_appointment_takes_the_obvious_spellings(string typed)
    {
        var form = new AppointmentEditViewModel { StartTimeEntry = typed, EndTimeEntry = typed };

        Assert.True(form.TryCommitTimes(), $"'{typed}' is a time a person types");
        Assert.Equal(new TimeSpan(9, 50, 0), form.StartTime);
        Assert.Equal(new TimeSpan(9, 50, 0), form.EndTime);
        Assert.Equal(string.Empty, form.TimeError);
    }

    [Fact]
    public void An_appointments_time_shows_up_typed_when_the_model_carries_one()
    {
        // Loading an appointment (or the New default) must fill the box the user edits, not just the model.
        var form = new AppointmentEditViewModel { StartTime = new TimeSpan(14, 5, 0) };

        Assert.Equal("14:05", form.StartTimeEntry);
    }

    [Fact]
    public void An_empty_appointment_time_is_no_time_rather_than_an_error()
    {
        // The all-day / floating case the form already supports: clearing the box is how a user says it.
        var form = new AppointmentEditViewModel { StartTime = new TimeSpan(9, 0, 0), StartTimeEntry = "", EndTimeEntry = "" };

        Assert.True(form.TryCommitTimes());
        Assert.Null(form.StartTime);
        Assert.Null(form.EndTime);
    }

    [Fact]
    public void An_appointment_refuses_a_value_that_is_not_a_time_and_keeps_the_old_one()
    {
        var form = new AppointmentEditViewModel { StartTime = new TimeSpan(9, 0, 0) };
        form.StartTimeEntry = "half past nine";

        Assert.False(form.TryCommitTimes());
        Assert.NotEqual(string.Empty, form.TimeError);           // the dialog shows this and stays open
        Assert.Equal(new TimeSpan(9, 0, 0), form.StartTime);     // nothing was silently dropped
    }

    [Fact]
    public void A_reminder_defaults_to_a_typed_time()
    {
        // The spinner's 09:00 default, now in the box the user can simply overtype. Constructed with a bare
        // client because nothing here reaches the wire — the default is the assertion.
        var reminders = new ReminderDialogViewModel(
            new SimplArchive.DesktopClient.Services.SimplArchiveApiClient("no-call-is-made"), "api/x/reminders", "x");

        Assert.Equal("09:00", reminders.ReminderTimeEntry);
    }
}
