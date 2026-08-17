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

    /// <summary>True when at least one CHECKED collection accepts writes — gates New/Edit.</summary>
    public bool CanCreate => Collections.Any(c => c.IsChecked && c.Writable);

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
    /// Creates a contact in the first ticked writable addressbook. NOT YET IMPLEMENTED: a contact IS a .vcf,
    /// so this needs the editor that composes one (the open question on the epic) — until then the button
    /// says so rather than silently doing nothing.
    /// </summary>
    [RelayCommand]
    public Task NewContactAsync()
    {
        Report(Strings.Get("ContactsNewNotYet"));
        return Task.CompletedTask;
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
            ("Personal / My Contacts", "#1e88e5", true),
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
    }

    private void Report(string message) => StatusReporter?.Invoke(message);
}
