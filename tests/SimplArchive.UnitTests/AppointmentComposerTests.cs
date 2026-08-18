using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;

namespace SimplArchive.UnitTests;

// The appointment editor rewrites a stored .ics, and the whole question is what happens to the parts of that
// component we do not model. A user fixes a typo in the title here; the reminder they set on their phone must
// still ring tomorrow.
//
// ADR 0631 decision 4, which exists because the prior art gets this wrong: SimplCalCon's ObjectComposer
// rebuilds the VEVENT from a StringBuilder and drops VALARM, CATEGORIES and every X- extension on every save.
// These tests pin the opposite, because the failure is invisible at the moment it happens and only surfaces on
// someone else's device days later.
public class AppointmentComposerTests
{
    private readonly AppointmentComposer _composer = new();

    // Shaped like a card a phone would write: the fields we model, standard ones we do not, two alarms (one
    // carrying its own X- property), attendees, and a VTIMEZONE the DTSTART depends on.
    private const string PhoneAuthored =
        "BEGIN:VCALENDAR\r\n"
        + "VERSION:2.0\r\n"
        + "PRODID:-//Some Phone//Calendar 1.0//EN\r\n"
        + "BEGIN:VTIMEZONE\r\n"
        + "TZID:Europe/Zurich\r\n"
        + "BEGIN:STANDARD\r\n"
        + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nTZNAME:CET\r\nDTSTART:19701025T030000\r\n"
        + "END:STANDARD\r\n"
        + "END:VTIMEZONE\r\n"
        + "BEGIN:VEVENT\r\n"
        + "UID:11111111-2222-3333-4444-555555555555\r\n"
        + "DTSTAMP:20260801T090000Z\r\n"
        + "DTSTART;TZID=Europe/Zurich:20260901T140000\r\n"
        + "DTEND;TZID=Europe/Zurich:20260901T150000\r\n"
        + "SUMMARY:Weekly sync\r\n"
        + "LOCATION:Room 3\r\n"
        + "DESCRIPTION:Agenda in the shared folder.\r\n"
        + "RRULE:FREQ=WEEKLY;BYDAY=TU\r\n"
        + "CATEGORIES:Work,Recurring\r\n"
        + "STATUS:CONFIRMED\r\n"
        + "TRANSP:OPAQUE\r\n"
        + "SEQUENCE:3\r\n"
        + "ORGANIZER;CN=Anna Meyer:mailto:anna@example.test\r\n"
        + "ATTENDEE;CN=Tom Fischer;PARTSTAT=ACCEPTED:mailto:tom@example.test\r\n"
        + "X-MY-CUSTOM-FLAG:keep-me\r\n"
        + "BEGIN:VALARM\r\n"
        + "ACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nDESCRIPTION:Reminder\r\nX-WR-ALARMUID:alarm-1\r\n"
        + "END:VALARM\r\n"
        + "BEGIN:VALARM\r\n"
        + "ACTION:AUDIO\r\nTRIGGER:-PT5M\r\n"
        + "END:VALARM\r\n"
        + "END:VEVENT\r\n"
        + "END:VCALENDAR\r\n";

    private const string Uid = "11111111-2222-3333-4444-555555555555";

    // Ical.Net reorders properties alphabetically and refolds, so a merged blob is never byte-identical to its
    // input. Survival is therefore asserted on the UNFOLDED text, never on byte equality.
    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty).Replace("\r\n\t", string.Empty);

    [Fact]
    public void Editing_a_modelled_field_leaves_every_unmodelled_property_intact()
    {
        var appointment = _composer.Read(PhoneAuthored);
        var merged = Unfold(_composer.Merge(PhoneAuthored, appointment with { Summary = "Weekly sync (moved)" }, Uid));

        Assert.Contains("SUMMARY:Weekly sync (moved)", merged);

        // The reminder is the whole point of decision 4 — both alarms, with their own properties.
        Assert.Equal(2, merged.Split("BEGIN:VALARM").Length - 1);
        Assert.Contains("TRIGGER:-PT15M", merged);
        Assert.Contains("TRIGGER:-PT5M", merged);
        Assert.Contains("X-WR-ALARMUID:alarm-1", merged);
        Assert.Contains("ACTION:AUDIO", merged);

        // …and everything else a user would notice missing.
        Assert.Contains("CATEGORIES:Work,Recurring", merged);
        Assert.Contains("STATUS:CONFIRMED", merged);
        Assert.Contains("TRANSP:OPAQUE", merged);
        Assert.Contains("SEQUENCE:3", merged);
        Assert.Contains("X-MY-CUSTOM-FLAG:keep-me", merged);
        Assert.Contains("PARTSTAT=ACCEPTED", merged);
        Assert.Contains("CN=Anna Meyer", merged);

        // The VTIMEZONE the DTSTART refers to must survive, or the time it names becomes unresolvable.
        Assert.Contains("BEGIN:VTIMEZONE", merged);
        Assert.Contains("TZID:Europe/Zurich", merged);
    }

    [Fact]
    public void The_uid_survives_so_a_later_sync_matches_rather_than_duplicates()
    {
        var merged = _composer.Merge(PhoneAuthored, _composer.Read(PhoneAuthored), Uid);

        Assert.Contains($"UID:{Uid}", merged);
        Assert.StartsWith("BEGIN:VCALENDAR", merged);
        Assert.Contains("END:VCALENDAR", merged);
    }

    [Fact]
    public void Reading_extracts_the_modelled_fields_in_the_appointments_own_zone()
    {
        var appointment = _composer.Read(PhoneAuthored);

        Assert.Equal("Weekly sync", appointment.Summary);
        Assert.Equal("Room 3", appointment.Location);
        Assert.Equal("Agenda in the shared folder.", appointment.Description);
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", appointment.RecurrenceRule);

        // The wall-clock as written — 14:00, NOT converted to the reader's zone (ADR 0631 decision 5).
        Assert.Equal(new DateTime(2026, 9, 1, 14, 0, 0), appointment.Start);
        Assert.Equal(new DateTime(2026, 9, 1, 15, 0, 0), appointment.End);
        Assert.Equal("Europe/Zurich", appointment.TimeZoneId);
        Assert.False(appointment.IsAllDay);
    }

    [Fact]
    public void The_series_is_edited_rather_than_whichever_component_comes_first()
    {
        // A series with one edited occurrence. Reading the FIRST component instead of the master is wrong the
        // moment this shape exists: the form would silently read, and then rewrite, that single occurrence
        // while telling the user they are editing the series. Ical.Net may serialize either one first, so the
        // override is placed first here to make the wrong behaviour fail rather than pass by luck.
        var withOverride =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Probe//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:u2\r\nDTSTAMP:20260801T090000Z\r\n"
            + "RECURRENCE-ID;TZID=Europe/Zurich:20260908T140000\r\n"
            + "DTSTART;TZID=Europe/Zurich:20260908T160000\r\nSUMMARY:Just this one\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:u2\r\nDTSTAMP:20260801T090000Z\r\n"
            + "DTSTART;TZID=Europe/Zurich:20260901T140000\r\nSUMMARY:The series\r\n"
            + "RRULE:FREQ=WEEKLY;BYDAY=TU\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        Assert.Equal("The series", _composer.Read(withOverride).Summary);

        var merged = Unfold(_composer.Merge(withOverride, _composer.Read(withOverride) with { Summary = "Renamed series" }, "u2"));

        Assert.Contains("SUMMARY:Renamed series", merged);
        Assert.Contains("SUMMARY:Just this one", merged);          // the override is untouched…
        Assert.DoesNotContain("SUMMARY:The series", merged);        // …and the master is what changed
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Zurich:20260908T140000", merged);
    }

    [Fact]
    public void A_floating_time_stays_floating()
    {
        // Floating means "this time, wherever you are". Attaching a zone on save silently changes what the
        // appointment means for everyone in another one, and no later client can undo it.
        var floating =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//P//EN\r\nBEGIN:VEVENT\r\nUID:u3\r\n"
            + "DTSTAMP:20260801T090000Z\r\nDTSTART:20260901T140000\r\nDTEND:20260901T150000\r\n"
            + "SUMMARY:Floating\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var read = _composer.Read(floating);
        Assert.Null(read.TimeZoneId);

        var merged = Unfold(_composer.Merge(floating, read with { Summary = "Still floating" }, "u3"));

        Assert.Contains("DTSTART:20260901T140000", merged);
        Assert.DoesNotContain("DTSTART;TZID=", merged);
        Assert.DoesNotContain("DTSTART:20260901T140000Z", merged);
    }

    [Fact]
    public void An_all_day_appointment_stays_a_date_and_never_gains_a_time()
    {
        var allDay =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//P//EN\r\nBEGIN:VEVENT\r\nUID:u4\r\n"
            + "DTSTAMP:20260801T090000Z\r\nDTSTART;VALUE=DATE:20260901\r\nDTEND;VALUE=DATE:20260902\r\n"
            + "SUMMARY:Holiday\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var read = _composer.Read(allDay);
        Assert.True(read.IsAllDay);

        var merged = Unfold(_composer.Merge(allDay, read with { Summary = "Public holiday" }, "u4"));

        Assert.Contains("DTSTART;VALUE=DATE:20260901", merged);
        Assert.Contains("DTEND;VALUE=DATE:20260902", merged);
        Assert.Contains("SUMMARY:Public holiday", merged);
    }

    [Fact]
    public void An_appointment_composed_from_nothing_is_well_formed()
    {
        // New Appointment: there is no existing blob to merge into.
        var merged = Unfold(_composer.Merge(null, Appointment.Empty with
        {
            Summary = "Kickoff",
            Start = new DateTime(2026, 10, 1, 9, 0, 0),
            End = new DateTime(2026, 10, 1, 10, 0, 0),
            TimeZoneId = "Europe/Zurich",
            Location = "Room 1",
        }, "new-uid-1"));

        Assert.StartsWith("BEGIN:VCALENDAR", merged);
        Assert.Contains("UID:new-uid-1", merged);
        Assert.Contains("SUMMARY:Kickoff", merged);
        Assert.Contains("DTSTART;TZID=Europe/Zurich:20261001T090000", merged);
        Assert.Contains("DTEND;TZID=Europe/Zurich:20261001T100000", merged);
        Assert.Contains("LOCATION:Room 1", merged);
        Assert.Contains("END:VCALENDAR", merged);
    }

    [Fact]
    public void Clearing_a_modelled_field_removes_it_rather_than_writing_an_empty_property()
    {
        var appointment = _composer.Read(PhoneAuthored);
        var merged = Unfold(_composer.Merge(PhoneAuthored, appointment with
        {
            Location = null,
            Description = null,
            RecurrenceRule = null,
        }, Uid));

        Assert.DoesNotContain("LOCATION:", merged);
        Assert.DoesNotContain("RRULE:", merged);

        // The alarm has its own DESCRIPTION, so clearing the event's must not take that with it.
        Assert.DoesNotContain("DESCRIPTION:Agenda in the shared folder.", merged);
        Assert.Contains("DESCRIPTION:Reminder", merged);

        // …while the unmodelled properties are still untouched, which is the point.
        Assert.Contains("CATEGORIES:Work,Recurring", merged);
        Assert.Contains("X-MY-CUSTOM-FLAG:keep-me", merged);
    }

    [Fact]
    public void A_recurrence_rule_survives_with_its_parts_intact()
    {
        // Measured, not assumed: Ical.Net CANONICALISES the order of the recur-rule-parts, so
        // "FREQ=MONTHLY;BYDAY=2TU;COUNT=10" comes back as "FREQ=MONTHLY;COUNT=10;BYDAY=2TU". RFC 5545 §3.3.10
        // says their order is not significant, so this is a reformatting rather than a change of meaning — but
        // it does mean the rule cannot be asserted textually, and a future reader should not read the
        // difference as a bug.
        var merged = Unfold(_composer.Merge(PhoneAuthored, _composer.Read(PhoneAuthored) with
        {
            RecurrenceRule = "FREQ=MONTHLY;BYDAY=2TU;COUNT=10",
        }, Uid));

        var emitted = merged.Split("\r\n").Single(l => l.StartsWith("RRULE:"))["RRULE:".Length..];
        Assert.Equal(
            ["BYDAY=2TU", "COUNT=10", "FREQ=MONTHLY"],
            emitted.Split(';').Order().ToArray());

        // And the form is stable: reading the merged blob back gives what a second edit would start from.
        Assert.Equal(emitted, _composer.Read(merged).RecurrenceRule);
    }

    [Fact]
    public void A_utc_time_stays_utc()
    {
        // The third shape a DTSTART can take, after zoned and floating: a Z suffix. It must not acquire a
        // TZID, and must not be silently reinterpreted as a local wall clock.
        var utc =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//P//EN\r\nBEGIN:VEVENT\r\nUID:u6\r\n"
            + "DTSTAMP:20260801T090000Z\r\nDTSTART:20260901T140000Z\r\nDTEND:20260901T150000Z\r\n"
            + "SUMMARY:In UTC\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var read = _composer.Read(utc);
        Assert.Equal("UTC", read.TimeZoneId);

        var merged = Unfold(_composer.Merge(utc, read with { Summary = "Still UTC" }, "u6"));

        Assert.Contains("DTSTART:20260901T140000Z", merged);
        Assert.Contains("DTEND:20260901T150000Z", merged);
        Assert.DoesNotContain("DTSTART;TZID=", merged);
    }

    [Fact]
    public void An_appointment_whose_length_is_a_duration_takes_an_explicit_end()
    {
        // An event carries either DTEND or DURATION, and Ical.Net refuses to hold both. A component written
        // with DURATION therefore needs it cleared before an edited end can go on — without that, saving any
        // change at all throws and the user loses the edit.
        var withDuration =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//P//EN\r\nBEGIN:VEVENT\r\nUID:u7\r\n"
            + "DTSTAMP:20260801T090000Z\r\nDTSTART;TZID=Europe/Zurich:20260901T140000\r\n"
            + "DURATION:PT1H\r\nSUMMARY:Hour long\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var read = _composer.Read(withDuration);
        var merged = Unfold(_composer.Merge(withDuration, read with
        {
            End = new DateTime(2026, 9, 1, 16, 0, 0),
        }, "u7"));

        Assert.Contains("DTEND;TZID=Europe/Zurich:20260901T160000", merged);
        Assert.DoesNotContain("DURATION:", merged);
    }

    [Fact]
    public void An_unreadable_appointment_is_refused_rather_than_replaced_with_a_stub()
    {
        // The editor's contract is that everything it does not model survives, and it keeps that by editing the
        // stored component in place. When the component cannot be read there is nothing to edit INTO, so
        // composing a fresh one would swap the originating client's content for a stub — silently, the moment
        // the user saved an unrelated typo. Refusing is the only answer consistent with the contract.
        //
        // The contact side never has to make this choice: a vCard merge is line-level, so text it cannot
        // interpret rides through verbatim. Using a library here buys correct component surgery and costs
        // exactly this case.
        Assert.Throws<UnreadableAppointmentException>(
            () => _composer.Merge("this is not a calendar", Appointment.Empty with { Summary = "Whatever" }, "u5"));

        Assert.Throws<UnreadableAppointmentException>(() => _composer.Read("this is not a calendar"));

        // New Appointment still works — there is no stored content to lose.
        Assert.Contains("SUMMARY:Fresh", _composer.Merge(null, Appointment.Empty with { Summary = "Fresh" }, "u5"));
    }
}
