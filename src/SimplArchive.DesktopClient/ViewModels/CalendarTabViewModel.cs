using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

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

    /// <summary>The day this appointment groups under — the list is read by day, not as a flat sequence.</summary>
    public DateOnly? Day => Start is { } start ? DateOnly.FromDateTime(start.LocalDateTime) : null;

    /// <summary>
    /// The full start, in the reader's own time zone, for the detail pane. It exists because binding the raw
    /// DateTimeOffset formats the stored OFFSET while <see cref="TimeRange"/> converts to local — so the same
    /// appointment read "When 11:00–12:00" and "Starts 09:00" in one pane, two lines apart. One instant must
    /// have one answer on the screen.
    /// </summary>
    public string StartsOn => Start is { } start ? start.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>"09:00–10:00", or empty when the appointment carries no time at all.</summary>
    public string TimeRange => (Start, End) switch
    {
        ({ } s, { } e) => $"{s.LocalDateTime:HH:mm}–{e.LocalDateTime:HH:mm}",
        ({ } s, null) => $"{s.LocalDateTime:HH:mm}",
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
/// Backs the desktop Calendar tab (#564): a FLAT checkbox list of calendars, the appointments of the checked
/// ones merged into one chronological list, and a detail pane. The twin of the Contacts tab (ADR 0511 keeps
/// the pair one surface), differing only where a calendar genuinely differs from an addressbook — the list is
/// ordered by time and grouped by day, because that is how anyone reads a calendar.
/// </summary>
public sealed partial class CalendarTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    /// <summary>Routes messages to the shared bottom status bar.</summary>
    public Action<string>? StatusReporter { get; set; }

    public ObservableCollection<CalendarCollectionViewModel> Collections { get; } = [];

    public ObservableCollection<AppointmentRowViewModel> Appointments { get; } = [];

    [ObservableProperty] private AppointmentRowViewModel? _selected;
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

    /// <summary>The checked calendars the caller may create in, in the order the tab lists them.</summary>
    public IReadOnlyList<CreateTarget> CreateTargets() =>
    [
        .. Collections
            .Where(c => c.IsChecked)
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
    public IEnumerable<AppointmentRowViewModel> VisibleAppointments =>
        Filter is { Length: > 0 } filter
            ? Appointments.Where(a =>
                a.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || a.Location.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            : Appointments;

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(VisibleAppointments));

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

    public void Setup(SimplArchiveApiClient api) => _api = api;

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
            List<Node> children;
            try
            {
                children = await _api.Documents.GetChildrenAsync(collection.Collection.Href("children"));
            }
            catch (Exception e)
            {
                // One unreadable calendar must not blank the whole tab — the others still have appointments.
                Report(string.Format(Strings.Get("StErrLoadCalendars"), e.Message));
                continue;
            }

            foreach (var child in children.Where(c => c.HasVersions))
            {
                rows.Add(new AppointmentRowViewModel
                {
                    Id = child.Id,
                    CollectionColor = collection.Color ?? "#8a8a8a",
                    CollectionName = collection.DisplayName,
                    Title = child.Name,
                    // Start/End/Location come from the Appointment mask's index data, which this listing does
                    // not carry — the same gap the Contacts tab has, and it closes the same way.
                    Links = child.Links ?? new Dictionary<string, string>(),
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
        OnPropertyChanged(nameof(VisibleAppointments));
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
            ("Personal / My Calendar", "#1e88e5", true),
            ("Team / Releases", "#8e24aa", false),
        };

        Collections.Clear();
        foreach (var (name, colour, personal) in calendars)
        {
            Collections.Add(new CalendarCollectionViewModel
            {
                Collection = new DavCollection(
                    Guid.NewGuid(), name, name.Split('/')[^1].Trim(), "calendar", colour, true, personal,
                    new Dictionary<string, string>()),
                Color = colour,
                IsChecked = true,
            });
        }

        // A fixed date, not "today": a screenshot that moves every day cannot be compared against the last one.
        var day = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            ("Sprint planning", 9, 10, "Room 2.14", 0),
            ("Architecture review", 11, 12, "Zoom", 1),
            ("Lunch with Marta", 12, 13, "Kornhausplatz", 0),
            ("Release 0.4.0 cut", 16, 17, string.Empty, 1),
        };

        Appointments.Clear();
        foreach (var (title, from, to, location, calendar) in items)
        {
            Appointments.Add(new AppointmentRowViewModel
            {
                Id = Guid.NewGuid(),
                CollectionColor = calendars[calendar].Item2,
                CollectionName = calendars[calendar].Item1,
                Title = title,
                Start = day.AddHours(from),
                End = day.AddHours(to),
                Location = location,
                Links = new Dictionary<string, string>(),
            });
        }

        Selected = Appointments[0];
        OnPropertyChanged(nameof(VisibleAppointments));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void Report(string message) => StatusReporter?.Invoke(message);
}
