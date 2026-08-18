using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using SimplArchive.Api.Errors.Exceptions.Documents;

namespace SimplArchive.Api.Documents;

/// <inheritdoc cref="IAppointmentComposer"/>
public sealed class AppointmentComposer : IAppointmentComposer
{
    private const string ProductId = "-//SimplArchive//Appointment Editor//EN";

    public Appointment Read(string blob)
    {
        if (Master(Load(blob)) is not { } master)
        {
            return Appointment.Empty;
        }

        var start = master.DtStart;
        var end = master.DtEnd;

        return new Appointment(
            Nonempty(master.Summary),
            start?.Value,
            end?.Value,
            start is { HasTime: false },
            ZoneOf(start),
            Nonempty(master.Location),
            Nonempty(master.Description),
            Nonempty(master.RecurrenceRule?.ToString()));
    }

    public string Merge(string? existingBlob, Appointment appointment, string uid)
    {
        var calendar = existingBlob is null ? NewCalendar() : Load(existingBlob);
        var master = Master(calendar);

        if (master is null)
        {
            master = new CalendarEvent { DtStamp = CalDateTime.UtcNow };
            calendar.Events.Add(master);
        }

        master.Uid = uid;

        // Only the six modelled fields are written. Everything else on the component — VALARM, CATEGORIES,
        // STATUS, ATTENDEE, X-* and any vendor sub-component — is left exactly as it was found.
        //
        // DTSTAMP is deliberately NOT refreshed. It is the stamp of the scheduling message an object was
        // created for, and this product never sends one (ADR 0628, and decision 3 of ADR 0631): clients here
        // sync on the ETag, so rewriting it would churn the blob to no reader's benefit.
        master.Summary = Nonempty(appointment.Summary);
        master.Location = Nonempty(appointment.Location);
        master.Description = Nonempty(appointment.Description);

        ApplyTiming(master, appointment);
        ApplyRecurrence(master, appointment.RecurrenceRule);

        return new CalendarSerializer().SerializeToString(calendar)
               ?? throw new InvalidOperationException("The iCalendar serializer returned nothing.");
    }

    // The master is the component WITHOUT a RECURRENCE-ID. Taking the first component instead is wrong the
    // moment a series has an edited occurrence: the override may be serialized first, and the form would then
    // read — and rewrite — that single occurrence while claiming to edit the series.
    private static CalendarEvent? Master(Calendar calendar) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar.Events.FirstOrDefault();

    // Writes the times back in the appointment's OWN zone (ADR 0631 decision 5) — no conversion happens
    // anywhere in this path, which is why a recurring appointment cannot drift across a daylight-saving
    // change. A floating value stays floating and an all-day one stays a DATE.
    private static void ApplyTiming(CalendarEvent master, Appointment appointment)
    {
        if (appointment.Start is not { } start)
        {
            return;
        }

        master.DtStart = ToCalDateTime(start, appointment.IsAllDay, appointment.TimeZoneId);

        // An event carries either DTEND or DURATION, never both, and Ical.Net enforces that on both setters:
        // it refuses a DURATION while DTEND is set. So a component that expresses its length as a DURATION has
        // to have it cleared before an explicit end can go on — and one that already has DTEND needs nothing,
        // because its DURATION is null by that same rule.
        if (appointment.End is { } end)
        {
            if (master.DtEnd is null)
            {
                master.Duration = null;
            }

            master.DtEnd = ToCalDateTime(end, appointment.IsAllDay, appointment.TimeZoneId);
        }
    }

    private static CalDateTime ToCalDateTime(DateTime value, bool isAllDay, string? timeZoneId) =>
        isAllDay
            ? new CalDateTime(DateOnly.FromDateTime(value))
            : new CalDateTime(DateOnly.FromDateTime(value), TimeOnly.FromDateTime(value), timeZoneId!);

    private static void ApplyRecurrence(CalendarEvent master, string? rule)
    {
        // The rule stays opaque — parsed only so it round-trips as a real RRULE, never expanded here.
        master.RecurrenceRule = Nonempty(rule) is { } text ? new RecurrencePattern(text) : null;
    }

    // null for a floating value, "UTC" for a Z-suffixed one, else the TZID as written.
    private static string? ZoneOf(CalDateTime? value) => value switch
    {
        null => null,
        { IsFloating: true } => null,
        _ => value.TzId,
    };

    // Refuses rather than falls back. Composing a fresh calendar for a blob we cannot read would replace the
    // originating client's content with a stub the moment the user saved an unrelated change — see
    // UnreadableAppointmentException for why that is the one thing this editor must not do.
    private static Calendar Load(string blob)
    {
        try
        {
            return Calendar.Load(blob) ?? throw new UnreadableAppointmentException();
        }
        catch (Exception failure) when (failure is not UnreadableAppointmentException)
        {
            throw new UnreadableAppointmentException();
        }
    }

    private static Calendar NewCalendar() => new() { ProductId = ProductId, Version = "2.0" };

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
