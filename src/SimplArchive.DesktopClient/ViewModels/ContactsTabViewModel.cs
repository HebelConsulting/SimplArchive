using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One contact row in the middle pane.</summary>
public sealed partial class ContactRowViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    /// <summary>The collection it came from — shown as a colour swatch when several are overlaid.</summary>
    public required string CollectionColor { get; init; }

    public required string CollectionName { get; init; }

    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _organization = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;

    /// <summary>The card's own picture once fetched, or null while it loads and when there is none.</summary>
    [ObservableProperty] private Bitmap? _photo;

    /// <summary>The letters drawn when there is no picture — shared with the web client so both agree.</summary>
    public string Initials => ContactInitials.From(FullName);

    /// <summary>
    /// Whether this contact HAS a picture, which is what the row asks before spending a request.
    /// </summary>
    /// <remarks>
    /// Answered by the rel the listing advertised, not by trying: a missing <c>photo</c> rel means the card
    /// carries no picture (ADR 0543), so the initials are the answer rather than a fallback from a 404.
    /// </remarks>
    public bool HasPhoto => Links.ContainsKey("photo");

    partial void OnFullNameChanged(string value) => OnPropertyChanged(nameof(Initials));

    /// <summary>The row's own advertised addresses — the pane acts from these, never from a composed URL.</summary>
    public required IReadOnlyDictionary<string, string> Links { get; init; }
}

/// <summary>One collection in the left pane: checked collections are overlaid in the list.</summary>
public sealed partial class ContactCollectionViewModel : ObservableObject
{
    public required DavCollection Collection { get; init; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _color;

    /// <summary>
    /// Raised when the tick changes, so the tab can re-read the merged list. Wired only AFTER the tab has
    /// finished populating: the initial ticks are set during the load, and a handler attached first would fire
    /// once per collection and re-read the list under itself.
    /// </summary>
    public Action? Toggled { get; set; }

    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke();

    public string DisplayName => Collection.DisplayName;

    public bool Writable => Collection.Writable;
}

/// <summary>
/// Backs the desktop Contacts tab (#564): a FLAT checkbox list of addressbooks, the contacts of the checked
/// ones, and a detail pane. The concept is SimplCalCon's, translated to the workbench's panes with the
/// Repositories tab as the reference (ADR 0511).
/// </summary>
/// <remarks>
/// Its own view-model on purpose: MainWindowViewModel is the largest entry on the 1000-line debt list, and a
/// tab's worth of state belongs to the tab (the same reasoning as CheckoutTabViewModel, and the direction
/// issue #517 wants). Everything it addresses comes from a rel the server advertised.
/// </remarks>
public sealed partial class ContactsTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    /// <summary>Routes messages to the shared bottom status bar.</summary>
    private readonly IShellContext _shell;

    public ContactsTabViewModel(IShellContext shell) => _shell = shell;

    public ObservableCollection<ContactCollectionViewModel> Collections { get; } = [];

    public ObservableCollection<ContactRowViewModel> Contacts { get; } = [];

    [ObservableProperty] private ContactRowViewModel? _selected;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _filter = string.Empty;

    /// <summary>
    /// What the list says when it has nothing to show. Two different sentences, deliberately: telling someone
    /// to tick a collection when they already have one ticked instructs them to do what they just did, and
    /// reads as the tab being broken rather than as the collection being empty.
    /// </summary>
    public string EmptyMessage => Collections.Any(c => c.IsChecked)
        ? Strings.Get("ContactsEmpty")
        : Strings.Get("ContactsNoneSelected");

    /// <summary>
    /// True when at least one CHECKED collection advertises the create — which gates New.
    /// </summary>
    /// <remarks>
    /// Asked of the <c>contacts</c> rel rather than of <c>Writable</c>. They are not the same question: the
    /// flag reports whether the caller may edit CONTENT, while the create needs the right to add sub-items, and
    /// gating on the wrong one either hides a create that would succeed or offers one the server refuses. A
    /// missing rel already means "not available to you, here, now" (ADR 0543), so this is the server's answer
    /// rather than the client's guess at it.
    /// </remarks>
    public bool CanCreate => CreateTargets().Count > 0;

    /// <summary>
    /// The checked addressbooks the caller may create in, in the order the tab lists them.
    /// </summary>
    /// <remarks>
    /// Gated on <c>CanCreateEntries</c>, NOT on the presence of the <c>contacts</c> rel: that rel now serves
    /// the LISTING too, so it is advertised to any reader, and gating on it would light New up for someone
    /// who cannot create and fail with a 403 on click (ADR 0543).
    /// </remarks>
    public IReadOnlyList<CreateTarget> CreateTargets() =>
    [
        .. Collections
            .Where(c => c.IsChecked && c.Collection.CanCreateEntries)
            .Select(c => (c.DisplayName, Href: c.Collection.HrefOrNull("contacts")))
            .Where(c => c.Href is not null)
            .Select(c => new CreateTarget(c.DisplayName, c.Href!)),
    ];

    /// <summary>Creates a contact from a filled-in form, then shows it selected in the list.</summary>
    /// <remarks>
    /// One request: the create takes the editor's whole resource, so nothing the user typed is left for a
    /// follow-up save that could fail and leave a half-filled contact behind.
    /// </remarks>
    public async Task CreateContactAsync(CreateTarget target, ContactEditViewModel form)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var createdId = await _api.StructuredEditors.CreateAsync(target.CreateHref, form.ToPayload());
            await ReloadContactsAsync();

            // Select what was just made — by the id the server returned, never by the name the form composed:
            // the server decides the filed name (it disambiguates a sibling clash), so matching on our own guess
            // at it silently selects nothing on exactly the second "Ada Lovelace". A create that leaves the list
            // looking unchanged reads as one that did not happen, and the row is what every later action is
            // addressed from (ADR 0559).
            Selected = Contacts.FirstOrDefault(c => c.Id == createdId) ?? Selected;
            Report(string.Format(Strings.Get("StContactCreated"), Selected?.FullName ?? string.Empty, target.DisplayName));
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrCreateContact"), e.Message));
        }
    }

    /// <summary>What the list actually shows: the filter applied over the merged contacts.</summary>
    public IEnumerable<ContactRowViewModel> VisibleContacts =>
        Filter is { Length: > 0 } filter
            ? Contacts.Where(c =>
                c.FullName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || c.Organization.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || c.Email.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            : Contacts;

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(VisibleContacts));

    /// <summary>
    /// Loads the selected contact's card for the edit form, or null when it cannot be edited here.
    /// </summary>
    /// <remarks>
    /// Addressed from the ROW the user clicked (ADR 0559), never from whatever the detail pane last finished
    /// loading — the pane's state describes the PREVIOUS selection for the whole window between a click and
    /// its response, and acting on it grants the edit against the wrong contact.
    ///
    /// The row's own link set comes from a children listing, which advertises what BROWSING needs (ADR 0557),
    /// so the card is reached by resolving the row's self address once and following what the document offers.
    /// A document that advertises no contact-card rel is the server saying this is not a contact (ADR 0543) —
    /// answered by disabling the affordance rather than by a failed request.
    /// </remarks>
    public async Task<StructuredEditorClient.Loaded<ContactEditViewModel>?> LoadCardAsync(ContactRowViewModel row)
    {
        if (_api is null || !row.Links.TryGetValue("self", out var self))
        {
            return null;
        }

        try
        {
            return await _api.StructuredEditors.ReadAsync(self, "contact-card", ContactEditViewModel.From);
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrLoadContacts"), e.Message));
            return null;
        }
    }

    /// <summary>
    /// Fills the form's raw box from the stored card, when the user opens the disclosure (#648).
    /// </summary>
    /// <remarks>
    /// On demand rather than with the card: a vCard carrying a photo is hundreds of kilobytes, and most edits
    /// never open the box. A card that advertises no <c>source</c> rel simply leaves it empty and read-only.
    /// </remarks>
    public async Task LoadRawAsync(StructuredEditorClient.Loaded<ContactEditViewModel> loaded, ContactEditViewModel form)
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
            Report(string.Format(Strings.Get("StErrLoadContacts"), e.Message));
        }
    }

    /// <summary>Saves an edited card and refreshes the list so the row shows what was stored.</summary>
    /// <remarks>
    /// A dirty raw box wins, and REPLACES the item. The two describe the same card and only one can be saved,
    /// so the form went read-only the moment the raw text changed — this is the other half of that rule.
    /// </remarks>
    public async Task SaveCardAsync(StructuredEditorClient.Loaded<ContactEditViewModel> loaded, ContactEditViewModel edited)
    {
        if (_api is null)
        {
            return;
        }

        if (edited.RawIsDirty)
        {
            await SaveRawAsync(loaded, edited);
            return;
        }

        try
        {
            await _api.StructuredEditors.SaveAsync(loaded.Href, edited.ToPayload(), loaded.ETag);
            Report(string.Format(Strings.Get("StContactSaved"), edited.StoredFormattedName ?? edited.FamilyName));
            await ReloadContactsAsync();
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrSaveContact"), e.Message));
        }
    }

    /// <summary>Replaces the stored card with the raw text, and says so plainly when the server refuses.</summary>
    /// <remarks>
    /// The refusals — text that is not a vCard, or one whose UID was changed — carry a message written for a
    /// person, so it is surfaced verbatim. "Saving failed" would leave the user with no idea which line to fix,
    /// which is a poor answer in an editor whose whole premise is that they can see what they are editing.
    /// </remarks>
    private async Task SaveRawAsync(StructuredEditorClient.Loaded<ContactEditViewModel> loaded, ContactEditViewModel edited)
    {
        try
        {
            await _api!.StructuredEditors.SaveRawAsync(loaded.Links, edited.RawText, edited.RawETag);
            Report(Strings.Get("StRawSaved"));
            await ReloadContactsAsync();
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrSaveContact"), e.Message));
        }
    }

    public void SetApi(SimplArchiveApiClient api) => _api = api;

    /// <summary>Loads the addressbooks; the caller's own is checked so the tab opens with content.</summary>
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
            var collections = await _api.DavCollections.ListAsync("addressbook");
            Collections.Clear();
            foreach (var collection in collections)
            {
                Collections.Add(new ContactCollectionViewModel
                {
                    Collection = collection,
                    Color = collection.Color,
                    // Personal defaults start checked: an empty tab that needs a click to show anything reads
                    // as broken (ADR 0550's family — an affordance nobody finds is one they conclude is absent).
                    IsChecked = collection.IsPersonalDefault,
                });
            }

            await ReloadContactsAsync();

            // Only now: see ContactCollectionViewModel.Toggled.
            foreach (var collection in Collections)
            {
                collection.Toggled = () => Safe.Fire(OnCollectionToggledAsync);
            }
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrLoadContacts"), e.Message));
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    /// <summary>The contacts of every checked addressbook, merged and sorted by name.</summary>
    public async Task ReloadContactsAsync()
    {
        if (_api is null)
        {
            return;
        }

        var rows = new List<ContactRowViewModel>();
        foreach (var collection in Collections.Where(c => c.IsChecked))
        {
            // The collection's OWN rel, followed with GET — the children listing carries a name and nothing
            // else, which is why Organization, Email and Phone were empty strings and the detail pane beside
            // them was blank by construction (#660).
            if (collection.Collection.HrefOrNull("contacts") is not { } entriesHref)
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
                // One unreadable collection must not blank the whole tab — the others still have contacts.
                Report(string.Format(Strings.Get("StErrLoadContacts"), e.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                rows.Add(new ContactRowViewModel
                {
                    Id = entry.Id,
                    CollectionColor = collection.Color ?? "#8a8a8a",
                    CollectionName = collection.DisplayName,
                    FullName = entry.FullName is { Length: > 0 } full ? full : entry.Name,
                    Organization = entry.Organization ?? string.Empty,
                    Email = entry.Email ?? string.Empty,
                    Phone = entry.Phone ?? string.Empty,
                    Links = entry.Links,
                });
            }
        }

        Contacts.Clear();
        foreach (var row in rows.OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            Contacts.Add(row);
        }

        // VisibleContacts is a PROJECTION, not a collection — mutating Contacts raises nothing for it, so the
        // list would keep rendering the previous addressbook's rows until the filter happened to change.
        OnPropertyChanged(nameof(VisibleContacts));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(EmptyMessage));

        // The list is already on screen with its initials by now; faces arrive as they load. Deliberately NOT
        // awaited before showing the rows — a tab that waits for every picture before drawing anything is a
        // blank pane for as long as the slowest image takes.
        _ = LoadPhotosAsync(rows);
    }

    /// <summary>
    /// Fetches the pictures for the rows that HAVE one, filling each in as it arrives.
    /// </summary>
    /// <remarks>
    /// Only for rows whose listing advertised a <c>photo</c> rel, so a book of contacts without pictures costs
    /// no requests at all — the listing already answered the question, and asking again per row is what
    /// ADR 0557 exists to stop. A failure leaves the initials in place: a face that will not load is a contact
    /// with initials, not an error worth interrupting anyone for.
    /// </remarks>
    private async Task LoadPhotosAsync(IEnumerable<ContactRowViewModel> rows)
    {
        if (_api is null)
        {
            return;
        }

        foreach (var row in rows.Where(r => r.HasPhoto))
        {
            try
            {
                var bytes = await _api.Core.Http.GetByteArrayAsync(row.Links["photo"]);
                using var stream = new MemoryStream(bytes);
                row.Photo = new Bitmap(stream);
            }
            catch (Exception)
            {
                // Left as initials. Not reported: the row is complete and correct without a picture, and one
                // failed avatar must not put an error banner in front of somebody reading an addressbook.
            }
        }
    }

    /// <summary>Re-reads the list when a collection is checked or unchecked.</summary>
    public Task OnCollectionToggledAsync() => ReloadContactsAsync();

    /// <summary>
    /// Fills the tab with plausible content for <c>--screenshot --contacts</c>. A native GUI needs a display,
    /// so this is how the layout is actually LOOKED at without one — two overlaid addressbooks, because one
    /// collection would not show what the colour swatch is for.
    /// </summary>
    public void PopulateDemoForScreenshot()
    {
        var books = new[]
        {
            ("Personal / My Addressbook", "#1e88e5", true),
            ("Sales / Customers", "#43a047", false),
        };

        Collections.Clear();
        foreach (var (name, colour, personal) in books)
        {
            Collections.Add(new ContactCollectionViewModel
            {
                Collection = new DavCollection(
                    Guid.NewGuid(), name, name.Split('/')[^1].Trim(), "addressbook", colour, true, personal, false,
                    new Dictionary<string, string>()),
                Color = colour,
                IsChecked = true,
            });
        }

        Contacts.Clear();
        var people = new[]
        {
            ("Alvarez, Marta", "Northwind Trading", "m.alvarez@northwind.example", "+41 44 555 01 22", 1),
            ("Berger, Jonas", "Hebel Consulting", "j.berger@hebel.example", "+41 31 555 88 40", 0),
            ("Cheng, Li Wei", "Northwind Trading", "l.cheng@northwind.example", "+41 44 555 01 87", 1),
            ("Dupont, Camille", string.Empty, "camille@dupont.example", "+33 1 45 55 12 09", 0),
        };

        foreach (var (name, org, email, phone, book) in people)
        {
            Contacts.Add(new ContactRowViewModel
            {
                Id = Guid.NewGuid(),
                CollectionColor = books[book].Item2,
                CollectionName = books[book].Item1,
                FullName = name,
                Organization = org,
                Email = email,
                Phone = phone,
                Links = new Dictionary<string, string>(),
            });
        }

        Selected = Contacts[0];
        OnPropertyChanged(nameof(VisibleContacts));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// Puts a message on the window's status line. Internal rather than private because this tab's own view
    /// reports through it — the view has the message, the tab owns the route.
    /// </summary>
    internal void Report(string message) => _shell.Report(message);
}
