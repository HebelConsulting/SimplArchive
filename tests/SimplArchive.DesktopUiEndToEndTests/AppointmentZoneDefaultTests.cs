using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopUiEndToEndTests;

// A NEW appointment is stamped with this machine's zone; an existing one is left exactly as stored (#1126).
//
// This supersedes ADR 0631 decision 5 for CREATE only. That decision left new entries floating — a time with
// no zone is "09:00 wherever you are" — on the grounds that stamping a zone merely inferred from the machine
// is how a floating time stops floating. True, and still true; what changed is the judgement that a person
// filing a meeting almost always means their own zone, and that the blank entry at the top of the list keeps
// a floating time one click away.
//
// EDITING is deliberately untouched, which is the half that protects the old decision: opening a stored
// floating entry and saving it must not silently stamp a zone it never had.
public class AppointmentZoneDefaultTests
{
    [Fact]
    public void A_new_appointment_is_stamped_with_this_machines_zone()
    {
        var form = new AppointmentEditViewModel();
        form.OpenForCreate([]);

        Assert.Equal(TimeZoneChoices.Local(), form.StartTimeZoneId);
        Assert.Equal(TimeZoneChoices.Local(), form.EndTimeZoneId);
    }

    // The pre-selected value must be one the picker actually offers. The conversion to IANA has to be the SAME
    // one the list went through, or a Windows host pre-fills a zone missing from its own dropdown — which is
    // why TimeZoneChoices.Local exists rather than each call site reading TimeZoneInfo.Local itself.
    [Fact]
    public void The_stamped_zone_is_one_the_picker_offers()
    {
        var form = new AppointmentEditViewModel();

        Assert.Contains(TimeZoneChoices.Local(), form.ZoneChoices);
    }

    // Floating stays reachable: the blank entry heads the list, so clearing the box is how a time goes back to
    // meaning "wherever you are".
    [Fact]
    public void The_list_still_offers_no_zone_at_all() =>
        Assert.Equal(string.Empty, new AppointmentEditViewModel().ZoneChoices[0]);

    // The half that keeps ADR 0631's decision alive where it still applies: the stamp happens in the CREATE
    // hook and nowhere else, so a form built from a stored entry carries exactly what was stored — blank
    // included. Asserted as "an un-opened form is blank", because that is the state the edit path fills from.
    [Fact]
    public void Nothing_but_opening_for_create_stamps_a_zone()
    {
        var form = new AppointmentEditViewModel();

        Assert.Equal(string.Empty, form.StartTimeZoneId);
        Assert.Equal(string.Empty, form.EndTimeZoneId);
    }
}
