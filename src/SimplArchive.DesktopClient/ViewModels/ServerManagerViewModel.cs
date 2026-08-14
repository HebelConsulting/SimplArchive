using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Theming;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the Ctrl/Cmd+P server manager (ADR "Desktop server configuration"): a list of configured servers on the
// left + a read-only data pane (Name / URL) on the right that Edit makes editable (Save/Cancel), plus a + to add
// a new one and a Remove. Changes persist to the server config file.
public sealed partial class ServerManagerViewModel : ObservableObject
{
    public ObservableCollection<ServerProfile> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedThemeName))]
    [NotifyPropertyChangedFor(nameof(SelectedEnvironmentName))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private ServerProfile? _selected;

    // Edit/add state — while editing, the data pane shows the editable fields bound to EditName/EditUrl.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isEditing;

    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editUrl = "";
    [ObservableProperty] private string _error = "";

    // The styles this installation can offer: the ones bundled with the client, plus anything dropped into a
    // themes/ folder beside it (ADR 0578). Read once — a picker that rescanned the disk on every keystroke
    // would be answering a question nobody asked.
    public ObservableCollection<ThemeCatalog.Entry> Themes { get; } =
        new(ThemeCatalog.Available());

    // Applied AS IT CHANGES rather than on Save: choosing a colour scheme from a list of names without seeing
    // it is guesswork, and the workbench is right there behind this window.
    [ObservableProperty] private ThemeCatalog.Entry? _editTheme;

    partial void OnEditThemeChanged(ThemeCatalog.Entry? value) => ThemeApplier.Apply(value?.Id);

    /// <summary>What the selected profile's style is CALLED — the read-only pane shows a name, not an id.</summary>
    public string SelectedThemeName => ThemeFor(Selected?.Theme).Name;

    // The environments a profile can declare itself to be (#501) — a fixed set, "(none)" first. No live
    // preview, unlike the style picker: the banner appears on the MAIN window, which doesn't exist yet while
    // this window is the one being used.
    public ObservableCollection<EnvironmentLevels.Level> Environments { get; } = new(EnvironmentLevels.All);

    [ObservableProperty] private EnvironmentLevels.Level? _editEnvironment;

    /// <summary>The selected profile's environment, by name — "(none)" when empty or unrecognised.</summary>
    public string SelectedEnvironmentName => EnvironmentFor(Selected?.Environment).Name;

    // True while EditUrl is a well-formed absolute http(s) address that a live probe confirmed is a SimplArchive
    // server — drives the light-green tint on the URL field while editing (issue #270).
    [ObservableProperty] private bool _editUrlIsOurServer;

    // Same, for the read-only URL of the currently-selected profile — so a merely-selected (not edited) profile
    // shows the green "this is our server" cue too (issue #270).
    [ObservableProperty] private bool _selectedIsOurServer;

    public bool HasSelection => Selected is not null;

    // The live "is this our server?" probe (issue #270) — injectable so a test needn't hit a real server.
    public Func<string, CancellationToken, Task<bool>> ServerIdentityCheck { get; set; } = ServerIdentity.IsSimplArchiveAsync;

    // How long to wait after the last keystroke before probing, so typing doesn't spam the network. Zeroed in
    // tests for determinism.
    internal TimeSpan ProbeDebounce { get; set; } = TimeSpan.FromMilliseconds(400);

    // Supersedes an in-flight probe on each keystroke / selection change.
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _selectedProbeCts;

    // The probe hits the network, so it's dormant until the window Activate()s the VM — otherwise constructing it
    // in a unit test would fire a request before the seam can be overridden.
    private bool _activated;

    // Called by the window once shown: enables the network-backed URL probes and checks the initially-selected
    // profile so its green "this is our server" cue shows on open (issue #270).
    public void Activate()
    {
        _activated = true;
        _ = ProbeSelectedAsync();
    }

    partial void OnSelectedChanged(ServerProfile? value)
    {
        // A different profile invalidates the previous read-only tint until a fresh probe confirms it.
        SelectedIsOurServer = false;
        if (_activated)
        {
            _ = ProbeSelectedAsync();
        }
    }

    // Probes the currently-selected profile's URL (not the edit field) so a merely-selected profile shows the
    // green cue in the read-only pane. No debounce — selection is a deliberate act, not per-keystroke.
    internal async Task ProbeSelectedAsync()
    {
        _selectedProbeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _selectedProbeCts = cts;

        var url = Selected?.ApiRootUrl?.Trim();
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SelectedIsOurServer = false;
            return;
        }

        try
        {
            var ok = await ServerIdentityCheck(url, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                SelectedIsOurServer = ok;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (Exception)
        {
            SelectedIsOurServer = false;
        }
    }

    partial void OnEditUrlChanged(string value)
    {
        // Any edit invalidates the previous positive result until a fresh probe confirms it.
        EditUrlIsOurServer = false;
        if (_activated)
        {
            _ = ProbeEditUrlAsync();
        }
    }

    // Validates EditUrl per keystroke and, when it's a well-formed absolute http(s) URL, debounces then probes
    // whether it's our server — setting EditUrlIsOurServer (the green tint). Best-effort; failures stay neutral.
    internal async Task ProbeEditUrlAsync()
    {
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;

        var url = EditUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            EditUrlIsOurServer = false;
            return;
        }

        try
        {
            if (ProbeDebounce > TimeSpan.Zero)
            {
                await Task.Delay(ProbeDebounce, cts.Token);
            }

            var ok = await ServerIdentityCheck(url, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                EditUrlIsOurServer = ok;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception)
        {
            EditUrlIsOurServer = false;
        }
    }

    public ServerManagerViewModel()
    {
        // Copy EVERY field. This copy used to carry only Name and ApiRootUrl while Persist() wrote Theme back
        // from it — so merely opening this window and saving anything (an edit, a remove) silently erased the
        // Theme of every profile not edited in that session. The copy and Persist() must agree on the field
        // list; when adding a profile field, both change or the new field is quietly eaten the same way.
        foreach (var t in ServerProfileStore.Load().Servers)
        {
            Servers.Add(new ServerProfile { Name = t.Name, ApiRootUrl = t.ApiRootUrl, Theme = t.Theme, Environment = t.Environment });
        }

        Selected = Servers.FirstOrDefault();
    }

    private bool CanEdit => Selected is not null && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        if (Selected is null)
        {
            return;
        }

        IsAdding = false;
        EditName = Selected.Name;
        EditUrl = Selected.ApiRootUrl;
        EditTheme = ThemeFor(Selected.Theme);
        EditEnvironment = EnvironmentFor(Selected.Environment);
        Error = "";
        IsEditing = true;
    }

    private bool CanAdd => !IsEditing;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        IsAdding = true;
        EditName = "";
        EditUrl = "";
        EditEnvironment = Environments.First(); // "(none)" — a new server declares nothing until told otherwise
        Error = "";
        IsEditing = true;
    }

    // The last entry can't be removed — there must always be a server to log into (ADR "Desktop logon window").
    private bool CanRemove => Selected is not null && !IsEditing && Servers.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (Selected is null || Servers.Count <= 1)
        {
            return;
        }

        Servers.Remove(Selected);
        Selected = Servers.FirstOrDefault();
        RemoveCommand.NotifyCanExecuteChanged();
        Persist();
    }

    [RelayCommand]
    private void Save()
    {
        var name = EditName.Trim();
        var url = EditUrl.Trim();
        if (name.Length == 0)
        {
            Error = Strings.Get("SmErrNameRequired");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Error = Strings.Get("SmErrUrlInvalid");
            return;
        }

        // A duplicate name (other than the one being edited) is rejected — the name is the profile's identity.
        var clashesWith = Servers.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clashesWith is not null && !(IsAdding == false && ReferenceEquals(clashesWith, Selected)))
        {
            Error = Strings.Get("SmErrDuplicateName");
            return;
        }

        if (IsAdding)
        {
            var profile = new ServerProfile
            {
                Name = name,
                ApiRootUrl = url,
                Theme = EditTheme?.Id,
                Environment = EditEnvironment?.Id ?? string.Empty,
            };
            Servers.Add(profile);
            Selected = profile;
            RemoveCommand.NotifyCanExecuteChanged();
        }
        else if (Selected is not null)
        {
            Selected.Name = name;
            Selected.ApiRootUrl = url;
            Selected.Theme = EditTheme?.Id;
            Selected.Environment = EditEnvironment?.Id ?? string.Empty;
            // Re-select to refresh the read-only pane bindings.
            var idx = Servers.IndexOf(Selected);
            var current = Selected;
            Selected = null;
            Selected = current;
            _ = idx;
        }

        IsEditing = false;
        IsAdding = false;
        Persist();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        IsAdding = false;
        Error = "";

        // The live preview is an edit like any other, so cancelling has to undo it too. Leaving the previewed
        // style on screen would be the one edit a Cancel button did not cancel.
        ThemeApplier.Apply(Selected?.Theme);
    }

    // A stored id that no longer resolves lands on the shipped design rather than an empty picker — styles come
    // and go, and a profile synced from another machine should still open.
    private ThemeCatalog.Entry ThemeFor(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Themes.First();

    // Unknown lands on "(none)" for the same reason ThemeFor falls back: the picker must open, whatever is on disk.
    private EnvironmentLevels.Level EnvironmentFor(string? id) =>
        Environments.FirstOrDefault(l => l.Id.Length > 0 && string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Environments.First();

    private void Persist()
    {
        var config = ServerProfileStore.Load();
        config.Servers = Servers
            .Select(t => new ServerProfile { Name = t.Name, ApiRootUrl = t.ApiRootUrl, Theme = t.Theme, Environment = t.Environment })
            .ToList();
        // Keep the remembered last-chosen server only if it still exists.
        if (config.LastServer is not null && Servers.All(t => !string.Equals(t.Name, config.LastServer, StringComparison.OrdinalIgnoreCase)))
        {
            config.LastServer = null;
        }

        ServerProfileStore.Save(config);
    }
}
