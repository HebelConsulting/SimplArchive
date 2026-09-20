using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

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

    /// <summary>
    /// WHICH BUILD is loaded, as the short sha — empty when the assembly carried no stamp (#1247).
    /// </summary>
    /// <remarks>
    /// The SHA alone, deliberately: every module reports version <c>1.0.0</c> because none declares a
    /// <c>&lt;Version&gt;</c>, so rendering the number would look like information and carry none. Shown at all
    /// because a stale module is otherwise invisible — it loads, seeds its masks and answers requests, and only
    /// features added after the deployed build are missing, which reads as "never implemented" (#1242).
    /// The web client shows the same thing in the same place; the two must not diverge (ADR 0511).
    /// </remarks>
    public string BuildSha
    {
        get
        {
            var plus = Module.Build?.IndexOf('+') ?? -1;
            if (plus < 0 || Module.Build is null || plus + 1 >= Module.Build.Length)
            {
                return string.Empty;
            }

            var sha = Module.Build[(plus + 1)..];
            return sha.Length > 8 ? sha[..8] : sha;
        }
    }

    /// <summary>The state line, one sentence per state (ADR 0740's escalate → grace → deactivate ladder).</summary>
    public string StatusText => Module switch
    {
        { Installed: false } => Strings.Get("ModNotInstalled"),
        { Activated: false } => Strings.Get("ModNotActivated"),
        { Active: true, InGrace: true, DeactivatesAt: { } grace } =>
            string.Format(Strings.Get("ModInGrace"), grace.InZone(SessionTimeZone.Current).DateTime.ToString("yyyy-MM-dd")),
        { Active: true, SupportContractEndDate: { } end } =>
            string.Format(Strings.Get("ModActiveUntil"), end.InZone(SessionTimeZone.Current).DateTime.ToString("yyyy-MM-dd")),
        _ => Strings.Get("ModDeactivated"),
    };

    public Avalonia.Media.IBrush StatusBrush => Module switch
    {
        { Installed: false } or { Activated: false } => OffBrush,
        { Active: true, InGrace: true } => GraceBrush,
        { Active: true } => OkBrush,
        _ => GraceBrush, // deactivated: the state an administrator must act on, same channel as in-grace
    };

    /// <summary>Whether to draw the content-health line at all. Only when something is WRONG: a healthy line
    /// on every module would be noise on a pane that is mostly rows, and the audit-webhook line above shows
    /// its healthy state because there is exactly one of it.</summary>
    public bool ContentFailing => Module.FailingContentSources > 0;

    /// <summary>The same sentence the web client renders (ADR 0511) — one surface, two clients.</summary>
    public string ContentHealthText => Module.FailingContentSources switch
    {
        0 => string.Empty,
        1 => string.Format(Strings.Get("ModContentFailingOne"), 1,
            Module.ContentFailingSince?.InZone(SessionTimeZone.Current).ToString("g") ?? string.Empty),
        var n => string.Format(Strings.Get("ModContentFailingMany"), n,
            Module.ContentFailingSince?.InZone(SessionTimeZone.Current).ToString("g") ?? string.Empty),
    };

    /// <summary>The last message, as a tooltip — on the line, never in it: a provider's sentence is the wrong
    /// length for a status row and this pane has no room to be wrong about that.</summary>
    public string ContentHealthTooltip => Module.ContentLastError ?? string.Empty;

    public string ActivateLabel => Strings.Get(Module.Activated ? "ModRenew" : "ModActivate");

    public bool CanActivate => Module.LicenseHref is not null;

    /// <summary>Only where the module DECLARED settings — the server withholds the rel otherwise, so no
    /// module is ever offered an empty form (ADR 0543/0772).</summary>
    public bool CanConfigure => Module.SettingsHref is not null;
}
