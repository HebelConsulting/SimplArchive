using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One cell of the month grid: a day, what falls on it, and how much did not fit.</summary>
/// <remarks>
/// A model rather than a formatting trick in the view, because "which entries are on this day" is the one
/// question the grid exists to answer, and it is worth being able to test without a window.
/// </remarks>
public sealed class CalendarDayViewModel
{
    public required DateOnly Day { get; init; }

    /// <summary>False for the leading/trailing days that keep the weeks whole — context, not content.</summary>
    public required bool InMonth { get; init; }

    public required bool IsToday { get; init; }

    public required IReadOnlyList<CalendarCellEntryViewModel> Entries { get; init; }

    /// <summary>How many more fall on this day than the cell can show.</summary>
    public required int Hidden { get; init; }

    public bool HasHidden => Hidden > 0;

    public string DayNumber => Day.Day.ToString(CultureInfo.CurrentCulture);

    public string MoreLabel => string.Format(CultureInfo.CurrentCulture, Strings.Get("CalendarMoreEntries"), Hidden);
}

/// <summary>One appointment as it appears in ONE day cell.</summary>
/// <remarks>
/// A multi-day entry occupies several cells, and what a cell shows depends on which cell it is: the day it
/// starts shows a time, the days it runs through show a continuation mark. That is a fact about the PAIR, so it
/// cannot live on the row — the same row object is in every cell it covers, and a flag on it would be rewritten
/// by whichever cell rendered last.
/// </remarks>
public sealed partial class CalendarCellEntryViewModel : ObservableObject
{
    public required AppointmentRowViewModel Row { get; init; }

    /// <summary>True on a covered day that is not the entry's first.</summary>
    public required bool Continues { get; init; }

    /// <summary>
    /// Whether this chip is the selected appointment — what the cell draws a highlight for.
    /// </summary>
    /// <remarks>
    /// A property on the CELL, updated in place, rather than a comparison against the tab's Selected. Rebuilding
    /// MonthDays on every click would work and is wrong twice: it destroys the very button being clicked, and a
    /// multi-day entry occupies several cells, so all of its chips must light up together.
    /// </remarks>
    [ObservableProperty] private bool _isSelected;

    public string Title => Row.Title;

    public string CollectionColor => Row.CollectionColor;

    /// <summary>The repeat marker, or empty. Bound directly so the cell needs no converter.</summary>
    public string RepeatMark => Row.RepeatMark;

    public bool Recurring => Row.Recurring;

    /// <summary>
    /// The start time, or the continuation mark on a day the entry merely runs through — repeating the start
    /// time on day three would state it began that morning.
    /// </summary>
    public string LeadText => Continues ? AppointmentRowViewModel.ContinuationMark : Row.StartTimeShort;
}

/// <summary>One appointment row in the middle pane.</summary>
public sealed partial class AppointmentRowViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    /// <summary>The calendar it came from — shown as a colour swatch when several are overlaid.</summary>
    public required string CollectionColor { get; init; }

    public required string CollectionName { get; init; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private DateTimeOffset? _start;
    [ObservableProperty] private DateTimeOffset? _end;
    [ObservableProperty] private string _location = string.Empty;

    /// <summary>A day rather than a moment: it has no time to show, and none should be invented (ADR 0647).</summary>
    public required bool AllDay { get; init; }

    /// <summary>The day as the server indexed it — set for an all-day entry, which has no Start instant.</summary>
    public DateOnly? IndexedDay { get; init; }

    /// <summary>The day this appointment groups under — the list is read by day, not as a flat sequence.</summary>
    public DateOnly? Day => IndexedDay ?? (Start is { } start ? DateOnly.FromDateTime(start.LocalDateTime) : null);

    /// <summary>The end as the server indexed it — <c>DTEND</c> verbatim, which iCalendar defines as EXCLUSIVE.</summary>
    public DateOnly? IndexedEndDay { get; init; }

    /// <summary>The stored <c>RRULE</c> as text, or null when the entry does not repeat.</summary>
    public string? Repeats { get; init; }

    /// <summary>
    /// Whether this entry repeats — and therefore whether the grid is showing less than the month holds.
    /// </summary>
    /// <remarks>
    /// The rule is never expanded, so a weekly rehearsal is drawn at its FIRST occurrence and nowhere else. A
    /// deliberate limitation; a silent one would be a grid quietly claiming the other three weeks are free.
    /// </remarks>
    public bool Recurring => !string.IsNullOrEmpty(Repeats);

    /// <summary>The marker itself, or empty — bound directly, so the view needs no converter.</summary>
    public string RepeatMark => Recurring ? AppointmentCoverage.RepeatMark : string.Empty;

    /// <summary>What a continuation cell shows where a starting one shows its time.</summary>
    public const string ContinuationMark = AppointmentCoverage.ContinuationMark;

    /// <summary>The last day this entry covers — see <see cref="AppointmentCoverage"/> for the arithmetic.</summary>
    public DateOnly? LastDay => AppointmentCoverage.LastDay(Day, AllDay, IndexedEndDay, End);

    /// <summary>Whether this entry is on show on <paramref name="day"/> — the grid's bucketing question.</summary>
    public bool CoversDay(DateOnly day) => AppointmentCoverage.CoversDay(day, Day, AllDay, IndexedEndDay, End);

    /// <summary>True on a covered day that is not the first — the cell reads as "still going", not "starts".</summary>
    public bool ContinuesOn(DateOnly day) => AppointmentCoverage.ContinuesOn(day, Day, AllDay, IndexedEndDay, End);

    /// <summary>
    /// The full start, in the reader's own time zone, for the detail pane. It exists because binding the raw
    /// DateTimeOffset formats the stored OFFSET while <see cref="TimeRange"/> converts to local — so the same
    /// appointment read "When 11:00–12:00" and "Starts 09:00" in one pane, two lines apart. One instant must
    /// have one answer on the screen.
    /// </summary>
    public string StartsOn => Start is { } start ? start.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Just the start — what a month cell has room for.</summary>
    /// <remarks>
    /// A day cell is about 85 px wide. Binding <see cref="TimeRange"/> there put "11:00–12:00" in an Auto column
    /// ahead of the title, so the title was trimmed to nothing and every entry read as a time and an ellipsis —
    /// identifying no more than the ISO-date names it sat beside. The end time is not what a cell answers, and
    /// the range is still on the row and in the detail pane where there is room for it. The web grid already
    /// showed the start alone; this is that pattern promoted into the desktop (ADR 0511).
    /// </remarks>
    public string StartTimeShort =>
        !AllDay && Start is { } at ? at.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>"09:00–10:00", the all-day marker, or empty when the entry carries no time at all.</summary>
    public string TimeRange => (AllDay, Start, End) switch
    {
        (true, _, _) => Strings.Get("CalendarAllDay"),
        (_, { } s, { } e) => $"{s.LocalDateTime:HH:mm}–{e.LocalDateTime:HH:mm}",
        (_, { } s, null) => $"{s.LocalDateTime:HH:mm}",
        _ => string.Empty,
    };

    /// <summary>The row's own advertised addresses — the pane acts from these, never from a composed URL.</summary>
    public required IReadOnlyDictionary<string, string> Links { get; init; }
}

/// <summary>One calendar in the left pane: checked calendars are overlaid in the list.</summary>
public sealed partial class CalendarCollectionViewModel : ObservableObject
{
    public required DavCollection Collection { get; init; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _color;

    /// <summary>
    /// Raised when the tick changes, so the tab can re-read the merged list. Wired only AFTER the tab has
    /// finished populating: the initial ticks are set during the load, and a handler attached first would fire
    /// once per calendar and re-read the list under itself.
    /// </summary>
    public Action? Toggled { get; set; }

    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke();

    public string DisplayName => Collection.DisplayName;

    public bool Writable => Collection.Writable;
}

/// <summary>
/// What the detail pane says about the selected appointment beyond what its row already carries (ADR 0690).
/// </summary>
/// <remarks>
/// Its own object rather than a dozen properties on the tab, so that clearing it on a selection change is one
/// assignment and cannot half-happen — the pane must never show one appointment's notes beside another's title
/// (ADR 0559).
/// </remarks>
public sealed partial class AppointmentDetailViewModel : ObservableObject
{
    /// <summary>The three readings of the time, or null for an all-day entry, which has no instant to place.</summary>
    public AppointmentTimeBlocks? Times { get; init; }

    public string Location { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public bool HasTimes => Times is not null;

    public bool HasViewerTime => Times?.Viewer is not null;

    public string UtcRange => Times?.Utc.Range ?? string.Empty;

    public IReadOnlyList<AppointmentTimeLine> RecordedLines => Times?.Recorded ?? [];

    public string ViewerRange => Times?.Viewer?.Range ?? string.Empty;

    /// <summary>The reader's own zone, named — the block exists precisely because it is not the recorded one.</summary>
    public string ViewerZone => Times?.Viewer?.Zone ?? string.Empty;

    // Each row is drawn only when it has something to say: on the Repositories tab the preview is what the
    // space is for, and an empty labelled row spends a line to report an absence (ADR 0550).
    public bool HasLocation => Location.Length > 0;

    public bool HasUrl => Url.Length > 0;

    public bool HasNotes => Notes.Length > 0;

    /// <summary>Reads the structured appointment resource into the pane's shape.</summary>
    public static AppointmentDetailViewModel From(System.Text.Json.JsonElement body)
    {
        var form = AppointmentEditViewModel.From(body);
        var times = AppointmentTimes.For(
            Combine(form.StartDate, form.StartTime),
            Combine(form.EndDate, form.EndTime),
            form.IsAllDay,
            form.StartTimeZoneId is { Length: > 0 } startZone ? startZone : null,
            form.EndTimeZoneId is { Length: > 0 } endZone ? endZone : null,
            TimeZoneInfo.Local);

        return new AppointmentDetailViewModel
        {
            Times = times is null ? null : AppointmentTimeBlocks.From(times, CultureInfo.CurrentCulture),
            Location = form.Location,
            Url = form.Url,
            Notes = form.Description,
        };
    }

    // The form holds the date and the time apart so an all-day entry can drop the time without inventing one;
    // the arithmetic wants them back together as the wall clock the file records.
    private static DateTime? Combine(DateTimeOffset? date, TimeSpan? time) =>
        date is { } d ? d.Date + (time ?? TimeSpan.Zero) : null;
}

/// <summary>
/// Backs the desktop Calendar tab (#564): a FLAT checkbox list of calendars, the appointments of the checked
/// ones merged into one chronological list, and a detail pane. The twin of the Contacts tab (ADR 0511 keeps
/// the pair one surface), differing only where a calendar genuinely differs from an addressbook — the list is
/// ordered by time and grouped by day, because that is how anyone reads a calendar.
/// </summary>
public sealed partial class CalendarTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    /// <summary>Routes messages to the shared bottom status bar.</summary>
    private readonly IShellContext _shell;

    public CalendarTabViewModel(IShellContext shell) => _shell = shell;

    public ObservableCollection<CalendarCollectionViewModel> Collections { get; } = [];

    public ObservableCollection<AppointmentRowViewModel> Appointments { get; } = [];

    [ObservableProperty] private AppointmentRowViewModel? _selected;

    /// <summary>The chips the month grid last built, so a selection can light them without a rebuild.</summary>
    private List<CalendarCellEntryViewModel> _monthCells = [];

    /// <summary>
    /// Selects the appointment a month chip stands for, so the detail pane fills the way a list row fills it.
    /// </summary>
    /// <remarks>
    /// The month grid had no interaction at all — the chips rendered and nothing could be clicked, so the whole
    /// pane beside it stayed empty in month view and the grid was read-only by accident rather than by decision.
    /// Takes the CELL and selects its Row: the same appointment appears in every cell it covers, and clicking
    /// any of them means the same thing.
    /// </remarks>
    [RelayCommand]
    private void SelectEntry(CalendarCellEntryViewModel? cell)
    {
        if (cell is null)
        {
            return;
        }

        Selected = cell.Row;
    }

    /// <summary>
    /// What the pane says about the selected appointment beyond its row — the zones, the link, the notes.
    /// </summary>
    /// <remarks>
    /// Null while nothing is selected AND for the whole window between a click and the read landing: the pane
    /// shows the row's own values immediately and fills the rest when it arrives, rather than leaving the
    /// PREVIOUS appointment's notes on screen, which is a claim about the wrong object (ADR 0559).
    /// </remarks>
    [ObservableProperty] private AppointmentDetailViewModel? _detail;

    // Every chip of the selected appointment lights up, not just the one clicked — a multi-day entry occupies
    // several cells, and highlighting one of them would say the others are a different entry.
    partial void OnSelectedChanged(AppointmentRowViewModel? value)
    {
        foreach (var cell in _monthCells)
        {
            cell.IsSelected = ReferenceEquals(cell.Row, value);
        }

        // Cleared FIRST, synchronously, so nothing of the previous appointment survives the load.
        Detail = null;
        if (value is not null)
        {
            Safe.Fire(() => LoadDetailAsync(value));
        }
    }

    /// <summary>
    /// Reads the selected appointment's own entry — one request, at the address its ROW advertised (ADR 0557).
    /// </summary>
    /// <remarks>
    /// Addressed from the row rather than from pane state: the pane describes whatever last finished loading,
    /// which during a load is a different appointment (ADR 0559). A row that advertises no <c>appointment</c>
    /// rel simply gets no extra detail — the server saying "not available here" (ADR 0543).
    /// </remarks>
    private async Task LoadDetailAsync(AppointmentRowViewModel row)
    {
        if (_api is null || !row.Links.TryGetValue("appointment", out var href))
        {
            return;
        }

        var detail = await _api.StructuredEditors.ReadAtAsync(href, AppointmentDetailViewModel.From);

        // The selection may have moved on while this was in flight — a superseded load stands down rather than
        // repainting the pane under a different subject.
        if (ReferenceEquals(Selected, row))
        {
            Detail = detail;
        }
    }
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _filter = string.Empty;

    /// <summary>
    /// What the list says when it has nothing to show. Two different sentences, deliberately: telling someone
    /// to tick a collection when they already have one ticked instructs them to do what they just did, and
    /// reads as the tab being broken rather than as the collection being empty.
    /// </summary>
    public string EmptyMessage => Collections.Any(c => c.IsChecked)
        ? Strings.Get("CalendarEmpty")
        : Strings.Get("CalendarNoneSelected");

    /// <summary>
    /// True when at least one CHECKED calendar advertises the create — which gates New.
    /// </summary>
    /// <remarks>
    /// Asked of the <c>appointments</c> rel, not of <c>Writable</c>: the flag reports the right to edit
    /// content, while the create needs the right to add sub-items, and gating on the wrong one either hides a
    /// create that works or offers one the server refuses (ADR 0543).
    /// </remarks>
    public bool CanCreate => CreateTargets().Count > 0;

    /// <summary>
    /// The checked calendars the caller may create in, in the order the tab lists them.
    /// </summary>
    /// <remarks>
    /// Gated on <c>CanCreateEntries</c>, NOT on the presence of the <c>appointments</c> rel: that rel now serves
    /// the LISTING too, so it is advertised to any reader, and gating on it would light New up for someone
    /// who cannot create and fail with a 403 on click (ADR 0543).
    /// </remarks>
    public IReadOnlyList<CreateTarget> CreateTargets() =>
    [
        .. Collections
            .Where(c => c.IsChecked && c.Collection.CanCreateEntries)
            .Select(c => (c.DisplayName, Href: c.Collection.HrefOrNull("appointments")))
            .Where(c => c.Href is not null)
            .Select(c => new CreateTarget(c.DisplayName, c.Href!)),
    ];

    /// <summary>Creates an appointment from a filled-in form, then shows it selected in the list.</summary>
    public async Task CreateAppointmentAsync(CreateTarget target, AppointmentEditViewModel form)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var createdId = await _api.StructuredEditors.CreateAsync(target.CreateHref, form.ToPayload());
            await ReloadAppointmentsAsync();

            // By the id the server returned, not by the summary the form holds — the server decides the filed
            // name, and a second "Quarterly review" would otherwise select the first one or nothing at all.
            Selected = Appointments.FirstOrDefault(a => a.Id == createdId) ?? Selected;
            Report(string.Format(Strings.Get("StApptCreated"), form.Summary, target.DisplayName));
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrCreateAppt"), e.Message));
        }
    }

    /// <summary>What the list actually shows: the filter applied over the merged appointments.</summary>
    public IEnumerable<AppointmentRowViewModel> VisibleAppointments
    {
        get
        {
            var rows = Appointments.AsEnumerable();
            if (Filter is { Length: > 0 } filter)
            {
                rows = rows.Where(a =>
                    a.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || a.Location.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
            }

            // The day filter narrows the LIST only: the grid shows a month, and a month narrowed to one day is
            // an empty month.
            if (!IsMonthView && DayFilter is { } day)
            {
                // Covers, not starts on: the filter answers the cell the user clicked, and a multi-day entry is
                // in that cell. Matching on the start day would hand back an emptier list than the grid showed.
                rows = rows.Where(a => a.CoversDay(day));
            }

            return rows;
        }
    }

    partial void OnFilterChanged(string value) => Refresh();

    // ── Month grid ───────────────────────────────────────────────────────────────────────────────────────
    //
    // 67 concerts across five months is not a list. The list answers "what is coming up"; the grid answers
    // "what does September look like", and shows that an act plays the same venue twice on one day — which is
    // what a date-only index could not even express (#660).

    /// <summary>Month grid or flat list. Opens on the month, which is what the tab is for.</summary>
    [ObservableProperty] private bool _isMonthView = true;

    /// <summary>The month on show — today's, which is what every calendar does and what a reader expects.</summary>
    [ObservableProperty] private DateOnly _month = Today.AddDays(1 - Today.Day);

    /// <summary>Set by a day cell's overflow; narrows the LIST to that day rather than growing the cell.</summary>
    [ObservableProperty] private DateOnly? _dayFilter;

    /// <summary>Rows in one day cell before the rest become "+N more" — see the web twin for the reasoning.</summary>
    private const int EntriesPerCell = 2;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>The culture's own first day of the week — Monday here, Sunday there; never a constant.</summary>
    private static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public string MonthName => Month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The seven column headings, rotated to start on the culture's own first day.</summary>
    public IReadOnlyList<string> WeekdayHeadings
    {
        get
        {
            var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
            return [.. Enumerable.Range(0, 7).Select(i => names[((int)FirstDay + i) % 7])];
        }
    }

    /// <summary>
    /// Forty-two cells: six weeks, always.
    /// </summary>
    /// <remarks>
    /// A fixed count keeps the grid from changing height as the user pages through months, which is otherwise
    /// a visible jolt on every click and moves the cell out from under the cursor.
    /// </remarks>
    public IReadOnlyList<CalendarDayViewModel> MonthDays
    {
        get
        {
            var lead = ((int)Month.DayOfWeek - (int)FirstDay + 7) % 7;
            var first = Month.AddDays(-lead);
            var visible = VisibleAppointments.ToList();

            var built = new List<CalendarCellEntryViewModel>();
            _monthCells = built;

            return
            [
                .. Enumerable.Range(0, 42).Select(i =>
                {
                    var day = first.AddDays(i);

                    // Chip-per-day, not a spanning bar: a bar across a week needs lane packing and absolute
                    // positioning, which is a different grid. So one entry yields one chip in each cell it covers.
                    var entries = visible
                        .Where(e => e.CoversDay(day))
                        // All-day first, then by time: an entry covering the whole day is context for the rest.
                        .OrderBy(e => !e.AllDay)
                        .ThenBy(e => e.Start)
                        .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    var cells = entries.Take(EntriesPerCell).Select(e => new CalendarCellEntryViewModel
                    {
                        Row = e,
                        Continues = e.ContinuesOn(day),
                        IsSelected = ReferenceEquals(e, Selected),
                    }).ToList();

                    // Kept so a later selection can light the right chips without rebuilding the grid.
                    built.AddRange(cells);

                    return new CalendarDayViewModel
                    {
                        Day = day,
                        InMonth = day.Month == Month.Month,
                        IsToday = day == Today,
                        Entries = cells,
                        Hidden = Math.Max(0, entries.Count - EntriesPerCell),
                    };
                }),
            ];
        }
    }

    [RelayCommand]
    private void PreviousMonth() => Month = Month.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => Month = Month.AddMonths(1);

    [RelayCommand]
    private void GoToday() => Month = Today.AddDays(1 - Today.Day);

    [RelayCommand]
    private void ShowMonth()
    {
        // The day filter belongs to the list it sent the user to; carrying it back into the grid would
        // silently hide every other day.
        DayFilter = null;
        IsMonthView = true;
    }

    [RelayCommand]
    private void ShowList() => IsMonthView = false;

    /// <summary>Hands a day to the list — which already has the columns, the filter and the selection.</summary>
    [RelayCommand]
    private void ShowDay(CalendarDayViewModel day)
    {
        DayFilter = day.Day;
        IsMonthView = false;
    }

    [RelayCommand]
    private void ClearDayFilter() => DayFilter = null;

    partial void OnMonthChanged(DateOnly value) => Refresh();

    partial void OnIsMonthViewChanged(bool value) => Refresh();

    partial void OnDayFilterChanged(DateOnly? value) => Refresh();

    /// <summary>Everything derived from the appointments, in one place so no view is left showing stale rows.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(VisibleAppointments));
        OnPropertyChanged(nameof(MonthDays));
        OnPropertyChanged(nameof(MonthName));
        OnPropertyChanged(nameof(DayFilterLabel));
    }

    /// <summary>The chip's text — a filter the user cannot see is one they conclude is a bug (ADR 0550).</summary>
    public string DayFilterLabel => DayFilter is { } day
        ? day.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>
    /// Loads the selected appointment's entry for the edit form, or null when it cannot be edited here.
    /// </summary>
    /// <remarks>
    /// Addressed from the ROW the user clicked (ADR 0559) and reached by following the document's own
    /// `appointment` rel (ADR 0543/0557) — a document that does not advertise it is the server saying this is
    /// not an appointment, which disables the affordance rather than producing a failed request.
    /// </remarks>
    public async Task<StructuredEditorClient.Loaded<AppointmentEditViewModel>?> LoadEntryAsync(AppointmentRowViewModel row)
    {
        if (_api is null || !row.Links.TryGetValue("self", out var self))
        {
            return null;
        }

        try
        {
            return await _api.StructuredEditors.ReadAsync(self, "appointment", AppointmentEditViewModel.From);
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrLoadCalendars"), e.Message));
            return null;
        }
    }

    /// <summary>Fills the form's raw box from the stored entry, when the user opens the disclosure (#648).</summary>
    /// <remarks>On demand rather than with the entry — see the contacts twin for why.</remarks>
    public async Task LoadRawAsync(StructuredEditorClient.Loaded<AppointmentEditViewModel> loaded, AppointmentEditViewModel form)
    {
        if (_api is null || form.RawLoaded)
        {
            return;
        }

        try
        {
            if (await _api.StructuredEditors.ReadRawAsync(loaded.Links) is { } raw)
            {
                form.SetRaw(raw.Text, raw.Format, raw.ETag);
            }
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrLoadCalendars"), e.Message));
        }
    }

    /// <summary>Saves an edited entry and refreshes the list so the row shows what was stored.</summary>
    /// <remarks>A dirty raw box wins and REPLACES the entry — see the contacts twin.</remarks>
    public async Task SaveEntryAsync(StructuredEditorClient.Loaded<AppointmentEditViewModel> loaded, AppointmentEditViewModel edited)
    {
        if (_api is null)
        {
            return;
        }

        if (edited.RawIsDirty)
        {
            try
            {
                await _api.StructuredEditors.SaveRawAsync(loaded.Links, edited.RawText, edited.RawETag);
                Report(Strings.Get("StRawSaved"));
                await ReloadAppointmentsAsync();
            }
            catch (Exception e)
            {
                Report(string.Format(Strings.Get("StErrSaveAppt"), e.Message));
            }

            return;
        }

        try
        {
            await _api.StructuredEditors.SaveAsync(loaded.Href, edited.ToPayload(), loaded.ETag);
            Report(string.Format(Strings.Get("StApptSaved"), edited.Summary));
            await ReloadAppointmentsAsync();
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrSaveAppt"), e.Message));
        }
    }

    public void SetApi(SimplArchiveApiClient api) => _api = api;

    /// <summary>Loads the calendars; the caller's own is checked so the tab opens with content.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var collections = await _api.DavCollections.ListAsync("calendar");
            Collections.Clear();
            foreach (var collection in collections)
            {
                Collections.Add(new CalendarCollectionViewModel
                {
                    Collection = collection,
                    Color = collection.Color,
                    IsChecked = collection.IsPersonalDefault,
                });
            }

            await ReloadAppointmentsAsync();

            // Only now: see CalendarCollectionViewModel.Toggled.
            foreach (var collection in Collections)
            {
                collection.Toggled = () => Safe.Fire(OnCollectionToggledAsync);
            }
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrLoadCalendars"), e.Message));
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    /// <summary>The appointments of every checked calendar, merged and ordered by start time.</summary>
    public async Task ReloadAppointmentsAsync()
    {
        if (_api is null)
        {
            return;
        }

        var rows = new List<AppointmentRowViewModel>();
        foreach (var collection in Collections.Where(c => c.IsChecked))
        {
            // The collection's OWN rel, followed with GET. The children listing was the wrong source: it
            // carries a name and nothing else, so every row's When and Where came out of an empty string and
            // the detail pane beside them was blank by construction (#660).
            if (collection.Collection.HrefOrNull("appointments") is not { } entriesHref)
            {
                continue; // a rel the server did not advertise means "not available here" (ADR 0543)
            }

            IReadOnlyList<DavEntry> entries;
            try
            {
                entries = await _api.DavCollections.ListEntriesAsync(entriesHref);
            }
            catch (Exception e)
            {
                // One unreadable calendar must not blank the whole tab — the others still have appointments.
                Report(string.Format(Strings.Get("StErrLoadCalendars"), e.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                rows.Add(new AppointmentRowViewModel
                {
                    Id = entry.Id,
                    CollectionColor = collection.Color ?? "#8a8a8a",
                    CollectionName = collection.DisplayName,
                    Title = entry.Name,
                    Start = entry.StartsAt,
                    End = entry.EndsAt,
                    Location = entry.Location ?? string.Empty,
                    AllDay = entry.AllDay,
                    IndexedDay = entry.Day,
                    IndexedEndDay = entry.EndDay,
                    Repeats = entry.Repeats,
                    Links = entry.Links,
                });
            }
        }

        Appointments.Clear();
        // Undated appointments last rather than first: a null start sorting to the top would put the least
        // informative rows where the eye lands.
        foreach (var row in rows.OrderBy(r => r.Start is null).ThenBy(r => r.Start).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Appointments.Add(row);
        }

        // VisibleAppointments is a PROJECTION, not a collection — mutating Appointments raises nothing for it.
        Refresh();
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Re-reads the list when a calendar is checked or unchecked.</summary>
    public Task OnCollectionToggledAsync() => ReloadAppointmentsAsync();

    /// <summary>
    /// Fills the tab with plausible content for <c>--screenshot --calendar</c>. A native GUI needs a display,
    /// so this is how the layout is actually LOOKED at without one — two overlaid calendars, because one would
    /// not show what the colour swatch is for.
    /// </summary>
    public void PopulateDemoForScreenshot()
    {
        var calendars = new[]
        {
            // The team calendar is read-only on purpose: it is what shows the lock (#…), and a demo where every
            // calendar is writable can never show that it works.
            ("Personal / My Calendar", "#1e88e5", true, true),
            ("Team / Releases", "#8e24aa", false, false),
        };

        Collections.Clear();
        foreach (var (name, colour, personal, writable) in calendars)
        {
            Collections.Add(new CalendarCollectionViewModel
            {
                Collection = new DavCollection(
                    Guid.NewGuid(), name, name.Split('/')[^1].Trim(), "calendar", colour, writable, personal, false,
                    new Dictionary<string, string>()),
                Color = colour,
                IsChecked = true,
            });
        }

        // A fixed date, not "today": a screenshot that moves every day cannot be compared against the last one.
        var day = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        // The last one spans three days on purpose: a multi-day entry is the case a chip-per-day grid can get
        // wrong invisibly, and a demo set where everything starts and ends within one afternoon never shows it.
        var items = new[]
        {
            // Sprint planning REPEATS: the marker is the only thing saying the grid is showing less than the
            // month holds, and a demo set in which nothing repeats can never show that it works.
            ("Sprint planning", 0, 9, 0, 10, "Room 2.14", 0, "FREQ=WEEKLY;BYDAY=TU"),
            ("Architecture review", 0, 11, 0, 12, "Zoom", 1, null),
            ("Lunch with Marta", 0, 12, 0, 13, "Kornhausplatz", 0, null),
            ("Release 0.4.0 cut", 0, 16, 0, 17, string.Empty, 1, null),
            ("Team offsite", 1, 9, 3, 17, "Interlaken", 1, null),
        };

        Appointments.Clear();
        foreach (var (title, fromDay, from, toDay, to, location, calendar, repeats) in items)
        {
            Appointments.Add(new AppointmentRowViewModel
            {
                Id = Guid.NewGuid(),
                CollectionColor = calendars[calendar].Item2,
                CollectionName = calendars[calendar].Item1,
                Title = title,
                AllDay = false,
                Start = day.AddDays(fromDay).AddHours(from),
                End = day.AddDays(toDay).AddHours(to),
                Location = location,
                Repeats = repeats,
                Links = new Dictionary<string, string>(),
            });
        }

        Selected = Appointments[0];

        // The detail the pane would have FETCHED, injected — a screenshot run reaches no server, and without
        // this the three time blocks (ADR 0690) simply would not be in the frame. Synthetic, like the tree
        // menu's admits: it checks how the pane RENDERS, not what the server sends. Two different zones
        // deliberately, since that is the case one field could never express and the case the viewer's own
        // block exists for.
        Detail = new AppointmentDetailViewModel
        {
            Times = AppointmentTimeBlocks.From(
                AppointmentTimes.For(
                    new DateTime(2026, 9, 1, 9, 0, 0), new DateTime(2026, 9, 1, 10, 0, 0),
                    isAllDay: false, "Europe/Zurich", "America/New_York", TimeZoneInfo.Local)!,
                CultureInfo.CurrentCulture),
            Location = "Room 2.14",
            Url = "https://meet.example.test/sprint",
            Notes = "Bring the backlog printout.",
        };

        Refresh();
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// Puts a message on the window's status line. Internal rather than private because this tab's own view
    /// reports through it — the view has the message, the tab owns the route.
    /// </summary>
    internal void Report(string message) => _shell.Report(message);
}
