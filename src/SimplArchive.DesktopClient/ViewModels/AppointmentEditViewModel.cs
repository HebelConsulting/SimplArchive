using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One invited attendee. Display only — this product never sends a scheduling message (ADR 0631).</summary>
public sealed record AttendeeRowViewModel(string Name, string Address, string Status);

/// <summary>
/// The appointment edit form's state (#564, ADR 0631). Holds the fields the form models; everything else on the
/// stored entry — VALARM above all — is preserved by the server's merge and never travels through here.
/// </summary>
public sealed partial class AppointmentEditViewModel : StructuredEditFormViewModel
{
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isAllDay;

    /// <summary>
    /// Date and time are separate so an all-day entry can drop the time without inventing one, and so the
    /// times shown are the APPOINTMENT'S own wall clock rather than a value converted into the viewer's zone
    /// (ADR 0631 decision 5).
    /// </summary>
    [ObservableProperty] private DateTimeOffset? _startDate;
    [ObservableProperty] private TimeSpan? _startTime;
    [ObservableProperty] private DateTimeOffset? _endDate;
    [ObservableProperty] private TimeSpan? _endTime;

    /// <summary>
    /// The zone the START is written in, chosen here rather than only displayed (ADR 0690). Nothing on this
    /// path CONVERTS a time — changing the zone re-labels the same wall clock, which is what keeps a weekly
    /// meeting from drifting across a daylight-saving change. The empty entry means a floating time, which
    /// stays floating.
    /// </summary>
    [ObservableProperty] private string _startTimeZoneId = "";

    /// <summary>
    /// The zone the END is written in, which iCalendar allows to DIFFER from the start's — a flight leaving
    /// Zurich at 09:00 and landing in Boston at 11:30 is one appointment with two zones, and one field for
    /// both makes it read as two and a half hours.
    /// </summary>
    [ObservableProperty] private string _endTimeZoneId = "";

    /// <summary>The event's web address (a meeting link, a ticket page). Absolute, or the save is refused.</summary>
    [ObservableProperty] private string _url = "";

    /// <summary>
    /// The zones the two pickers offer: IANA ids, with an empty first entry meaning "floating".
    /// </summary>
    /// <remarks>
    /// Shared with the web client (<c>TimeZoneChoices</c>) rather than built here, because a Windows host
    /// names its zones differently from the .ics format — offering the machine's own spelling would write a
    /// TZID no other calendar client can resolve.
    /// </remarks>
    public IReadOnlyList<string> ZoneChoices { get; } = [string.Empty, .. SimplArchive.Presentation.TimeZoneChoices.All()];

    /// <summary>
    /// The RRULE as stored. READ-ONLY this round: editing recurrence properly means choosing between this
    /// occurrence / this and following / all events, and those operations are not built yet — so a rule box
    /// here would silently apply every change to the whole series, including occurrences already past. It is
    /// still sent back verbatim, because the server's merge clears a rule it is handed as null.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>Human wording for <see cref="RecurrenceRule"/>, or empty when the entry does not repeat.</summary>
    public string RecurrenceText => Describe(RecurrenceRule);

    public bool Repeats => !string.IsNullOrWhiteSpace(RecurrenceRule);

    /// <summary>Who is invited and how they replied. Shown, never edited (ADR 0631 decision 3).</summary>
    public ObservableCollection<AttendeeRowViewModel> Attendees { get; } = [];

    /// <summary>How many reminders the entry carries. Shown so the form can say one is set (decision 4).</summary>
    public int ReminderCount { get; set; }

    public bool HasReminders => ReminderCount > 0;

    public bool HasAttendees => Attendees.Count > 0;

    public static AppointmentEditViewModel From(JsonElement body)
    {
        var model = new AppointmentEditViewModel
        {
            Summary = Text(body, "summary"),
            Location = Text(body, "location"),
            Description = Text(body, "description"),
            IsAllDay = body.TryGetProperty("isAllDay", out var allDay) && allDay.ValueKind == JsonValueKind.True,
            // The per-endpoint zones, falling back to the single one a server predating them would send.
            StartTimeZoneId = First(Text(body, "startTimeZoneId"), Text(body, "timeZoneId")),
            EndTimeZoneId = First(Text(body, "endTimeZoneId"), Text(body, "startTimeZoneId"), Text(body, "timeZoneId")),
            Url = Text(body, "url"),
            RecurrenceRule = Text(body, "recurrenceRule") is { Length: > 0 } rule ? rule : null,
            ReminderCount = body.TryGetProperty("reminderCount", out var count) && count.TryGetInt32(out var n) ? n : 0,
        };

        if (Parse(Text(body, "start")) is { } start)
        {
            model.StartDate = new DateTimeOffset(start.Date, TimeSpan.Zero);
            model.StartTime = start.TimeOfDay;
        }

        if (Parse(Text(body, "end")) is { } end)
        {
            model.EndDate = new DateTimeOffset(end.Date, TimeSpan.Zero);
            model.EndTime = end.TimeOfDay;
        }

        if (body.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var attendee in attendees.EnumerateArray())
            {
                model.Attendees.Add(new AttendeeRowViewModel(
                    Text(attendee, "name"), Text(attendee, "address"), Text(attendee, "status")));
            }
        }

        return model;
    }

    /// <summary>
    /// A new entry opens on the next full hour, running an hour (#631). Not left blank: an appointment with no
    /// date is the one field a person must fill before the form means anything, and defaulting it to a
    /// plausible slot is what turns "New appointment → type a title → Save" into the whole interaction.
    /// </summary>
    /// <remarks>
    /// Local wall-clock, and deliberately no <see cref="TimeZoneId"/>: the times a person types here are the
    /// ones they mean, and stamping a zone we merely inferred from the machine is how a floating time stops
    /// floating (ADR 0631 decision 5). The editor never converts one either.
    /// </remarks>
    protected override void OnOpenedForCreate()
    {
        var now = DateTime.Now;
        var start = DateTime.SpecifyKind(now.Date.AddHours(now.Hour + 1), DateTimeKind.Unspecified);
        var end = start.AddHours(1);

        // Kind.Unspecified is REQUIRED, not tidiness: DateTimeOffset(dateTime, TimeSpan.Zero) throws when the
        // value's Kind is Local, and DateTime.Now.Date is Local — so the obvious spelling crashes the dialog on
        // every machine east or west of UTC. Which is also what a zero offset means here: the form holds a wall
        // clock and the zone travels separately, the same shape `From` produces when it parses a stored time.
        StartDate = new DateTimeOffset(start.Date, TimeSpan.Zero);
        StartTime = start.TimeOfDay;
        EndDate = new DateTimeOffset(end.Date, TimeSpan.Zero);
        EndTime = end.TimeOfDay;
    }

    public object ToPayload() => new
    {
        summary = Null(Summary),
        start = Combine(StartDate, StartTime),
        end = Combine(EndDate, EndTime),
        isAllDay = IsAllDay,
        startTimeZoneId = Null(StartTimeZoneId),
        endTimeZoneId = Null(EndTimeZoneId),
        location = Null(Location),
        description = Null(Description),
        url = Null(Url),

        // Sent back unchanged. The field is not editable here, and the merge clears a rule handed to it as
        // null — so omitting it would silently un-repeat every recurring appointment anyone opened.
        recurrenceRule = RecurrenceRule,
    };

    /// <summary>
    /// The wall clock the form holds, serialized WITHOUT an offset. An offset would assert a zone, and the
    /// zone travels separately in timeZoneId — attaching one here is how a floating time stops floating.
    /// </summary>
    private static string? Combine(DateTimeOffset? date, TimeSpan? time) =>
        date is not { } d
            ? null
            : (d.Date + (IsAllDayLike(time) ? TimeSpan.Zero : time!.Value))
                .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static bool IsAllDayLike(TimeSpan? time) => time is null;

    /// <summary>The first non-empty of the candidates — how a newer field falls back to the one it supersedes.</summary>
    private static string First(params string[] candidates) =>
        Array.Find(candidates, c => !string.IsNullOrEmpty(c)) ?? string.Empty;

    private static DateTime? Parse(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// Plain wording for the common rules, and the rule itself for anything else. Deliberately not a full
    /// RRULE renderer: this is a read-only line, and inventing prose for a rule we half-understand would say
    /// something confidently wrong about when the appointment repeats.
    /// </summary>
    private static string Describe(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return "";
        }

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            if (split.Length == 2)
            {
                parts[split[0]] = split[1];
            }
        }

        var simple = parts.Count == 1 && parts.ContainsKey("FREQ");
        return simple
            ? parts["FREQ"].ToUpperInvariant() switch
            {
                "DAILY" => Strings.Get("ApptRepeatsDaily"),
                "WEEKLY" => Strings.Get("ApptRepeatsWeekly"),
                "MONTHLY" => Strings.Get("ApptRepeatsMonthly"),
                "YEARLY" => Strings.Get("ApptRepeatsYearly"),
                _ => rule,
            }
            : rule;
    }
}
