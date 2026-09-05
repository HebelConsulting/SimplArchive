using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The Tenant tab's Modules section (ADRs 0740/0743): what this host carries × what this tenant activated,
// each row deriving its state line from the same arithmetic the server derives `active` from — and the
// activation act, which the pane's dialog performs against the row's advertised `license` rel.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<ModuleRowViewModel> TenantModules { get; } = [];

    // The list's own `license-documents` rel (ADR 0557: captured where the collection was read).
    private string? _licenseDocumentsHref;

    [ObservableProperty] private bool _tenantModulesLoaded;

    public bool TenantModulesEmpty => TenantModulesLoaded && TenantModules.Count == 0;

    public async Task LoadTenantModulesAsync()
    {
        if (_api is null || LastTenantSettings is not { } settings)
        {
            return;
        }

        try
        {
            // Null = the rel is absent: not available to this caller, here, now (ADR 0543).
            var catalog = await _api.Admin.GetModulesAsync(settings);
            TenantModules.Clear();
            _licenseDocumentsHref = catalog?.LicenseDocumentsHref;
            foreach (var module in catalog?.Items ?? [])
            {
                TenantModules.Add(new ModuleRowViewModel(module));
            }

            TenantModulesLoaded = catalog is not null;
            OnPropertyChanged(nameof(TenantModulesEmpty));
        }
        catch (Exception)
        {
            ReportError(Strings.Get("StErrLoadTenant"));
        }
    }

    public (AdminClient? Admin, string? LicenseDocumentsHref) TenantModuleActivationContext() =>
        (_api?.Admin, _licenseDocumentsHref);
}

/// <summary>One module row — an immutable snapshot; the list reloads after every act.</summary>
public sealed class ModuleRowViewModel(AdminClient.ModuleInfo module)
{
    private static readonly Avalonia.Media.IBrush OkBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2e7d32"));
    private static readonly Avalonia.Media.IBrush GraceBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e65100"));
    private static readonly Avalonia.Media.IBrush OffBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9e9e9e"));

    public AdminClient.ModuleInfo Module { get; } = module;

    public string DisplayName => Module.DisplayName;

    /// <summary>The state line, one sentence per state (ADR 0740's escalate → grace → deactivate ladder).</summary>
    public string StatusText => Module switch
    {
        { Installed: false } => Strings.Get("ModNotInstalled"),
        { Activated: false } => Strings.Get("ModNotActivated"),
        { Active: true, InGrace: true, DeactivatesAt: { } grace } =>
            string.Format(Strings.Get("ModInGrace"), grace.LocalDateTime.ToString("yyyy-MM-dd")),
        { Active: true, SupportContractEndDate: { } end } =>
            string.Format(Strings.Get("ModActiveUntil"), end.LocalDateTime.ToString("yyyy-MM-dd")),
        _ => Strings.Get("ModDeactivated"),
    };

    public Avalonia.Media.IBrush StatusBrush => Module switch
    {
        { Installed: false } or { Activated: false } => OffBrush,
        { Active: true, InGrace: true } => GraceBrush,
        { Active: true } => OkBrush,
        _ => GraceBrush, // deactivated: the state an administrator must act on, same channel as in-grace
    };

    public string ActivateLabel => Strings.Get(Module.Activated ? "ModRenew" : "ModActivate");

    public bool CanActivate => Module.LicenseHref is not null;
}
