using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the Ctrl/Cmd+P tenant manager (ADR "Desktop tenant configuration"): a list of configured deployments on
// the left + a read-only data pane (Name / URL) on the right that Edit makes editable (Save/Cancel), plus a +
// to add a new one and a Remove. Changes persist to the tenant config file.
public sealed partial class TenantManagerViewModel : ObservableObject
{
    public ObservableCollection<TenantProfile> Tenants { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private TenantProfile? _selected;

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

    partial void OnSelectedChanged(TenantProfile? value)
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

    public TenantManagerViewModel()
    {
        foreach (var t in TenantProfileStore.Load().Tenants)
        {
            Tenants.Add(new TenantProfile { Name = t.Name, ApiRootUrl = t.ApiRootUrl });
        }

        Selected = Tenants.FirstOrDefault();
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
        Error = "";
        IsEditing = true;
    }

    // The last entry can't be removed — there must always be a tenant to log into (ADR "Desktop logon window").
    private bool CanRemove => Selected is not null && !IsEditing && Tenants.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (Selected is null || Tenants.Count <= 1)
        {
            return;
        }

        Tenants.Remove(Selected);
        Selected = Tenants.FirstOrDefault();
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
            Error = "A name is required.";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Error = "The URL must be an absolute http(s) address.";
            return;
        }

        // A duplicate name (other than the one being edited) is rejected — the name is the profile's identity.
        var clashesWith = Tenants.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clashesWith is not null && !(IsAdding == false && ReferenceEquals(clashesWith, Selected)))
        {
            Error = "A tenant with that name already exists.";
            return;
        }

        if (IsAdding)
        {
            var profile = new TenantProfile { Name = name, ApiRootUrl = url };
            Tenants.Add(profile);
            Selected = profile;
            RemoveCommand.NotifyCanExecuteChanged();
        }
        else if (Selected is not null)
        {
            Selected.Name = name;
            Selected.ApiRootUrl = url;
            // Re-select to refresh the read-only pane bindings.
            var idx = Tenants.IndexOf(Selected);
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
    }

    private void Persist()
    {
        var config = TenantProfileStore.Load();
        config.Tenants = Tenants.Select(t => new TenantProfile { Name = t.Name, ApiRootUrl = t.ApiRootUrl }).ToList();
        // Keep the remembered last-chosen tenant only if it still exists.
        if (config.LastTenant is not null && Tenants.All(t => !string.Equals(t.Name, config.LastTenant, StringComparison.OrdinalIgnoreCase)))
        {
            config.LastTenant = null;
        }

        TenantProfileStore.Save(config);
    }
}
