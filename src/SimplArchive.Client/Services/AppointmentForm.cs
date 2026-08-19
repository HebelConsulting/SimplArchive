using System.Globalization;
using System.Text.Json;

namespace SimplArchive.Client.Services;

/// <summary>One invited attendee. Display only — this product never sends a scheduling message (ADR 0631).</summary>
public sealed record AttendeeRow(string Name, string Address, string Status);

/// <summary>
/// The web appointment form's state (#631) — the twin of the desktop's <c>AppointmentEditViewModel</c>.
/// </summary>
/// <remarks>
/// Everything the form does not show — VALARM above all — is preserved by the server's merge and never travels
/// through here.
/// </remarks>
public sealed class AppointmentForm
{
    public string Summary { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsAllDay { get; set; }

    /// <summary>
    /// Date and time are separate so an all-day entry can drop the time without inventing one, and so the times
    /// shown are the APPOINTMENT'S own wall clock rather than a value converted into the viewer's zone
    /// (ADR 0631 decision 5).
    /// </summary>
    public DateTime? StartDate { get; set; }

    public TimeSpan? StartTime { get; set; }

    public DateTime? EndDate { get; set; }

    public TimeSpan? EndTime { get; set; }

    /// <summary>
    /// The appointment's own zone, carried through a save untouched. Null means a floating time, which stays
    /// floating — nothing on this path converts one, which is what keeps a weekly meeting from drifting across
    /// a daylight-saving change.
    /// </summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// The RRULE as stored, shown but not edited: editing recurrence properly means choosing between this
    /// occurrence / this and following / all, and those operations do not exist yet. Sent back verbatim,
    /// because the server's merge clears a rule handed to it as null.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    public bool Repeats => !string.IsNullOrWhiteSpace(RecurrenceRule);

    public int ReminderCount { get; set; }

    public List<AttendeeRow> Attendees { get; } = [];

    public bool CanEdit { get; set; } = true;

    /// <summary>The "Advanced: the stored item" disclosure's state (#648).</summary>
    public RawSourceState Raw { get; } = new();

    /// <summary>
    /// Whether the structured fields accept input. They go read-only while the raw text is dirty: the two
    /// describe the same item and only one is about to be saved, so leaving both live would let a user type
    /// into fields that are then discarded without a word (ADR 0550).
    /// </summary>
    public bool StructuredEnabled => CanEdit && !Raw.IsDirty;


    /// <summary>Reads the API's appointment resource into the form.</summary>
    public static AppointmentForm From(JsonElement body)
    {
        var form = new AppointmentForm
        {
            Summary = ContactCardForm.Text(body, "summary"),
            Location = ContactCardForm.Text(body, "location"),
            Description = ContactCardForm.Text(body, "description"),
            IsAllDay = body.TryGetProperty("isAllDay", out var allDay) && allDay.ValueKind == JsonValueKind.True,
            TimeZoneId = ContactCardForm.Text(body, "timeZoneId") is { Length: > 0 } tz ? tz : null,
            RecurrenceRule = ContactCardForm.Text(body, "recurrenceRule") is { Length: > 0 } rule ? rule : null,
            ReminderCount = body.TryGetProperty("reminderCount", out var count) && count.TryGetInt32(out var n) ? n : 0,
        };

        if (Parse(ContactCardForm.Text(body, "start")) is { } start)
        {
            form.StartDate = start.Date;
            form.StartTime = start.TimeOfDay;
        }

        if (Parse(ContactCardForm.Text(body, "end")) is { } end)
        {
            form.EndDate = end.Date;
            form.EndTime = end.TimeOfDay;
        }

        foreach (var attendee in ContactCardForm.Array(body, "attendees"))
        {
            form.Attendees.Add(new AttendeeRow(
                ContactCardForm.Text(attendee, "name"),
                ContactCardForm.Text(attendee, "address"),
                ContactCardForm.Text(attendee, "status")));
        }

        return form;
    }

    /// <summary>
    /// A new entry opens on the next full hour, running an hour (#631) — the same default as the desktop.
    /// </summary>
    /// <remarks>
    /// Local wall clock, and deliberately no <see cref="TimeZoneId"/>: the times a person types are the ones
    /// they mean, and stamping a zone inferred from the browser is how a floating time stops floating.
    /// </remarks>
    public static AppointmentForm ForCreate()
    {
        var now = DateTime.Now;
        var start = now.Date.AddHours(now.Hour + 1);

        return new AppointmentForm
        {
            StartDate = start.Date,
            StartTime = start.TimeOfDay,
            EndDate = start.AddHours(1).Date,
            EndTime = start.AddHours(1).TimeOfDay,
        };
    }

    /// <summary>The body a save or a create sends.</summary>
    public object ToPayload() => new
    {
        summary = Null(Summary),
        start = Combine(StartDate, StartTime),
        end = Combine(EndDate, EndTime),
        isAllDay = IsAllDay,
        timeZoneId = TimeZoneId,
        location = Null(Location),
        description = Null(Description),

        // Sent back unchanged. The field is not editable here, and the merge clears a rule handed to it as
        // null — so omitting it would silently un-repeat every recurring appointment anyone opened.
        recurrenceRule = RecurrenceRule,
    };

    /// <summary>
    /// The wall clock the form holds, serialized WITHOUT an offset. An offset would assert a zone, and the zone
    /// travels separately in <c>timeZoneId</c> — attaching one here is how a floating time stops floating.
    /// </summary>
    private static string? Combine(DateTime? date, TimeSpan? time) =>
        date is not { } d
            ? null
            : (d.Date + (time ?? TimeSpan.Zero)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime? Parse(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
