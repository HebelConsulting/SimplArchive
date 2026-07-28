using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    public bool HasSelection => Selected is not null;

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
