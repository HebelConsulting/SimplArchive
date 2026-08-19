using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One contact row in the middle pane.</summary>
public sealed partial class ContactRowViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    /// <summary>The collection it came from — shown as a colour swatch when several are overlaid.</summary>
    public required string CollectionColor { get; init; }

    public required string CollectionName { get; init; }

    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _organization = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _phone = "";

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
    public Action<string>? StatusReporter { get; set; }

    public ObservableCollection<ContactCollectionViewModel> Collections { get; } = [];

    public ObservableCollection<ContactRowViewModel> Contacts { get; } = [];

    [ObservableProperty] private ContactRowViewModel? _selected;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _filter = "";

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

    /// <summary>The checked addressbooks the caller may create in, in the order the tab lists them.</summary>
    public IReadOnlyList<CreateTarget> CreateTargets() =>
    [
        .. Collections
            .Where(c => c.IsChecked)
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

    public void Setup(SimplArchiveApiClient api) => _api = api;

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
            List<Node> children;
            try
            {
                children = await _api.Documents.GetChildrenAsync(collection.Collection.Href("children"));
            }
            catch (Exception e)
            {
                // One unreadable collection must not blank the whole tab — the others still have contacts.
                Report(string.Format(Strings.Get("StErrLoadContacts"), e.Message));
                continue;
            }

            foreach (var child in children.Where(c => c.HasVersions))
            {
                rows.Add(new ContactRowViewModel
                {
                    Id = child.Id,
                    CollectionColor = collection.Color ?? "#8a8a8a",
                    CollectionName = collection.DisplayName,
                    FullName = child.Name,
                    Links = child.Links ?? new Dictionary<string, string>(),
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
                    Guid.NewGuid(), name, name.Split('/')[^1].Trim(), "addressbook", colour, true, personal,
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

    private void Report(string message) => StatusReporter?.Invoke(message);
}
