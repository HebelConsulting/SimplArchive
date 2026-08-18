using Ical.Net;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The display-only facts about a stored appointment: who is invited, and whether a reminder is set.
/// </summary>
/// <remarks>
/// Kept off <see cref="Appointment"/> on purpose. That record is what the form sends back, so anything on it
/// is something a <c>PUT</c> is expected to honour — and neither of these may be written here. Attendees are
/// read-only because this product never sends a scheduling message (ADR 0631 decision 3), and the reminder
/// count exists so the form can say a reminder is set without implying it can be changed (decision 4).
/// Modelling them as editable fields is how they would quietly become editable later.
/// </remarks>
public static class AppointmentDisplay
{
    public sealed record InvitedAttendee(string? Name, string? Address, string? Status);

    public static IReadOnlyList<InvitedAttendee> Attendees(string blob) =>
        Master(blob) is not { } master
            ? []
            : [.. master.Attendees.Select(a => new InvitedAttendee(
                Nonempty(a.CommonName),
                Nonempty(a.Value?.ToString())?.Replace("mailto:", string.Empty, StringComparison.OrdinalIgnoreCase),
                Nonempty(a.ParticipationStatus)))];

    /// <summary>How many reminders the entry carries. Never zeroed by an edit — the composer does not touch them.</summary>
    public static int ReminderCount(string blob) => Master(blob)?.Alarms.Count ?? 0;

    // Same master rule as the composer: the component WITHOUT a RECURRENCE-ID, so a series with an edited
    // occurrence reports the series' attendees rather than that one occurrence's.
    private static Ical.Net.CalendarComponents.CalendarEvent? Master(string blob)
    {
        try
        {
            var calendar = Calendar.Load(blob);
            return calendar?.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null)
                   ?? calendar?.Events.FirstOrDefault();
        }
        catch (Exception)
        {
            // Display-only: an unreadable blob shows nothing here. The composer is what refuses the EDIT, and
            // it is reached before this on any path that writes.
            return null;
        }
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
