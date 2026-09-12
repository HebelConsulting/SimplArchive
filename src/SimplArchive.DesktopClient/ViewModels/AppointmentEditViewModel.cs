using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.Localization;

using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One invited attendee. Display only — this product never sends a scheduling message (ADR 0631).</summary>
public sealed record AttendeeRowViewModel(string Name, string Address, string Status);

/// <summary>
/// The appointment edit form's state (#564, ADR 0631). Holds the fields the form models; everything else on the
/// stored entry — VALARM above all — is preserved by the server's merge and never travels through here.
/// </summary>
public sealed partial class AppointmentEditViewModel : StructuredEditFormViewModel
{
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isAllDay;

    /// <summary>
    /// Date and time are separate so an all-day entry can drop the time without inventing one, and so the
    /// times shown are the APPOINTMENT'S own wall clock rather than a value converted into the viewer's zone
    /// (ADR 0631 decision 5).
    /// </summary>
    [ObservableProperty] private DateTime? _startDate;
    // The TIME the user edits is typed, not spun (#1057, the typed-not-picked principle): a person setting
    // an appointment knows the time, and four keystrokes beat hunting a spinner. StartTime/EndTime stay the
    // model values every caller composes from; the Entry strings are what the dialog binds, parsed on commit.
    [ObservableProperty] private string _startTimeEntry = string.Empty;

    [ObservableProperty] private string _endTimeEntry = string.Empty;

    /// <summary>The inline refusal for an unparseable time — empty when there is nothing to say.</summary>
    [ObservableProperty] private string _timeError = string.Empty;

    [ObservableProperty] private TimeSpan? _startTime;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private TimeSpan? _endTime;

    // Keep the typed surface showing whatever the model holds — loading an appointment, or the New default.
    partial void OnStartTimeChanged(TimeSpan? value) => StartTimeEntry = Typed(value);

    partial void OnEndTimeChanged(TimeSpan? value) => EndTimeEntry = Typed(value);

    private static string Typed(TimeSpan? value) =>
        value is { } v ? TimeOnly.FromTimeSpan(v).ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Parses what was typed into the model's times. False (with <see cref="TimeError"/> set) when a value is
    /// not a time — the dialog stays open and says so, rather than closing on a silently dropped entry.
    /// </summary>
    /// <remarks>
    /// An EMPTY entry is not an error: an appointment with no time is the all-day/floating case the form
    /// already supports, and clearing the box is how a user says that.
    /// </remarks>
    public bool TryCommitTimes()
    {
        if (!SimplArchive.Presentation.DocumentDateFormat.TryParseTypedTime(StartTimeEntry, out var start)
            || !SimplArchive.Presentation.DocumentDateFormat.TryParseTypedTime(EndTimeEntry, out var end))
        {
            TimeError = Strings.Get("TimeEntryInvalid");
            return false;
        }

        TimeError = string.Empty;
        StartTime = start?.ToTimeSpan();
        EndTime = end?.ToTimeSpan();
        return true;
    }

    /// <summary>
    /// The zone the START is written in, chosen here rather than only displayed (ADR 0690). Nothing on this
    /// path CONVERTS a time — changing the zone re-labels the same wall clock, which is what keeps a weekly
    /// meeting from drifting across a daylight-saving change. The empty entry means a floating time, which
    /// stays floating.
    /// </summary>
    [ObservableProperty] private string _startTimeZoneId = string.Empty;

    /// <summary>
    /// The zone the END is written in, which iCalendar allows to DIFFER from the start's — a flight leaving
    /// Zurich at 09:00 and landing in Boston at 11:30 is one appointment with two zones, and one field for
    /// both makes it read as two and a half hours.
    /// </summary>
    [ObservableProperty] private string _endTimeZoneId = string.Empty;

    /// <summary>The event's web address (a meeting link, a ticket page). Absolute, or the save is refused.</summary>
    [ObservableProperty] private string _url = string.Empty;

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
    /// The RRULE as stored, and what a save sends back. Editable through <see cref="RepeatKey"/> and
    /// <see cref="RepeatUntil"/> for the repeats the shared list offers (#1133); a richer rule that arrived
    /// over CalDAV is shown read-only rather than replaced by the nearest button, which on the next save
    /// would turn "every second Tuesday" into "every Tuesday".
    /// </summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>Human wording for <see cref="RecurrenceRule"/>, or empty when the entry does not repeat.</summary>
    public string RecurrenceText => Describe(RecurrenceRule);

    public bool Repeats => !string.IsNullOrWhiteSpace(RecurrenceRule);

    /// <summary>One repeat as the picker shows it: the shared key, this client's own wording.</summary>
    /// <remarks>
    /// The RULES are decided once in <see cref="RepeatChoices"/> so the two clients cannot offer different
    /// repeats; the LABELS stay each client's, because they come from the localized resources.
    /// </remarks>
    public sealed record RepeatOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>The repeats this editor offers, in the order the shared list gives them.</summary>
    public IReadOnlyList<RepeatOption> RepeatOptions { get; } =
    [
        .. RepeatChoices.All.Select(choice => new RepeatOption(choice.Key, Strings.Get(choice.Key switch
        {
            RepeatChoices.Daily => "RepeatDaily",
            RepeatChoices.Weekdays => "RepeatWeekdays",
            RepeatChoices.Weekly => "RepeatWeekly",
            RepeatChoices.Monthly => "RepeatMonthly",
            RepeatChoices.Yearly => "RepeatYearly",
            _ => "RepeatNone",
        }))),
    ];

    /// <summary>Which occurrences a save changes, as the API names it — null for an entry that does not repeat.</summary>
    /// <remarks>
    /// Set from the scope dialog just before the save, together with <see cref="RecurrenceId"/>. Null sends no
    /// scope at all, which is what every client did before the choice existed.
    /// </remarks>
    public string? Scope { get; set; }

    /// <summary>WHICH occurrence <see cref="Scope"/> is about — the instant the listing gave for the row.</summary>
    public string? RecurrenceId { get; set; }

    /// <summary>The selected option, which is what the picker binds to.</summary>
    public RepeatOption? SelectedRepeat
    {
        get => RepeatOptions.FirstOrDefault(option => option.Key == RepeatKey);
        set => RepeatKey = value?.Key ?? RepeatChoices.None;
    }

    /// <summary>Which repeat is selected — the shared key.</summary>
    /// <remarks>
    /// Kept as the KEY rather than the rule, so the picker's selection survives an UNTIL being set or cleared:
    /// the same repeat with a different end is still the same choice.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatsOnAChoice))]
    [NotifyPropertyChangedFor(nameof(ChoosesWeekdays))]
    [NotifyPropertyChangedFor(nameof(SelectedRepeat))]
    private string _repeatKey = RepeatChoices.None;

    /// <summary>When the repeat stops, or null for a series with no end.</summary>
    /// <remarks>
    /// A booking or a maintenance block MUST have one — two endless claims cannot be compared for overlap, so
    /// the server refuses an unbounded claim (#1133). An availability window may be left open: an offer takes
    /// nothing from anyone.
    /// </remarks>
    [ObservableProperty] private DateTime? _repeatUntil;

    /// <summary>True when the selected repeat is one this editor can express — what the UNTIL row is shown for.</summary>
    public bool RepeatsOnAChoice => RepeatKey != RepeatChoices.None;

    /// <summary>One weekday a weekly repeat may fall on.</summary>
    public sealed partial class WeekdayChoice : ObservableObject
    {
        public required DayOfWeek Day { get; init; }

        /// <summary>The culture's own short name — "Mo" here, "Mon" there; never a hardcoded letter.</summary>
        public required string Label { get; init; }

        [ObservableProperty] private bool _isChecked;
    }

    /// <summary>
    /// The seven days a weekly repeat may be ticked on, in the CULTURE's own order (#1136).
    /// </summary>
    /// <remarks>
    /// Drawn Monday-first here and Sunday-first there, because that is a display question — while the RULE's
    /// order is fixed, so the same repeat reads as the same rule in every session.
    /// </remarks>
    public IReadOnlyList<WeekdayChoice> Weekdays { get; } = BuildWeekdays();

    /// <summary>Shown only for a WEEKLY repeat: a daily or monthly one has no weekday to choose.</summary>
    public bool ChoosesWeekdays => RepeatKey == RepeatChoices.Weekly;

    private static IReadOnlyList<WeekdayChoice> BuildWeekdays()
    {
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;

        return [.. Enumerable.Range(0, 7)
            .Select(offset => (DayOfWeek)((first + offset) % 7))
            .Select(day => new WeekdayChoice { Day = day, Label = names[(int)day] })];
    }

    /// <summary>
    /// True when the stored rule is richer than the offered list, so the editor states it instead of offering
    /// to replace it.
    /// </summary>
    public bool RepeatIsRicherThanOffered => Repeats && RepeatChoices.KeyFor(RepeatChoices.WithoutUntil(RecurrenceRule)) is null;

    /// <summary>Loads the two picker values from the stored rule.</summary>
    public void ReadRepeatFromRule()
    {
        RepeatKey = RepeatChoices.KeyFor(RepeatChoices.WithoutUntil(RecurrenceRule)) ?? RepeatChoices.None;
        RepeatUntil = RepeatChoices.UntilOf(RecurrenceRule) is { } until ? until.ToDateTime(TimeOnly.MinValue) : null;

        var chosen = RepeatChoices.DaysOf(RecurrenceRule);
        foreach (var weekday in Weekdays)
        {
            weekday.IsChecked = chosen.Contains(weekday.Day);
        }
    }

    /// <summary>Writes the two picker values back into the rule a save sends.</summary>
    /// <remarks>
    /// A rule richer than the list is left ALONE: the picker never claimed it, so it must not overwrite it.
    /// </remarks>
    public void WriteRepeatIntoRule()
    {
        if (RepeatIsRicherThanOffered)
        {
            return;
        }

        // The days are part of the RULE, not a separate field: ticking none leaves a plain weekly repeat,
        // which already means "the entry's own weekday" (#1136).
        var rule = RepeatChoices.RuleFor(RepeatKey);
        if (RepeatKey == RepeatChoices.Weekly)
        {
            rule = RepeatChoices.WithDays(rule, Weekdays.Where(w => w.IsChecked).Select(w => w.Day));
        }

        RecurrenceRule = RepeatChoices.WithUntil(
            rule,
            RepeatUntil is { } until ? DateOnly.FromDateTime(until) : null);
    }

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

        // The picker's two values, read from the rule that arrived (#1133).
        model.ReadRepeatFromRule();

        if (Parse(Text(body, "start")) is { } start)
        {
            model.StartDate = start.Date;
            model.StartTime = start.TimeOfDay;
        }

        if (Parse(Text(body, "end")) is { } end)
        {
            model.EndDate = end.Date;
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
        StartDate = start.Date;
        StartTime = start.TimeOfDay;
        EndDate = end.Date;
        EndTime = end.TimeOfDay;

        // ...stamped with THIS machine's zone (#1126), which supersedes the floating default described above
        // for CREATE only. Editing still shows whatever was stored, blank included — see TimeZoneChoices.Local.
        StartTimeZoneId = EndTimeZoneId = SimplArchive.Presentation.TimeZoneChoices.Local();
    }

    public object ToPayload()
    {
        // The picker writes into the rule before it is sent — never the reverse, so a rule richer than the
        // offered list survives a save untouched (#1133).
        WriteRepeatIntoRule();
        return Payload();
    }

    private object Payload() => new
    {
        scope = Scope,
        recurrenceId = RecurrenceId,
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
