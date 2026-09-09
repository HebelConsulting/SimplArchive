using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// A module's per-tenant configuration (ADR 0772): a form rendered from what the MODULE declared, read and
/// written at the address its row advertised.
/// </summary>
/// <remarks>
/// A SECRET is write-only. The server reports whether one is set, never its value, so the box starts empty
/// even when configured and an empty box means "leave it alone" — which is also why the save is a merge: a
/// full replacement would blank every credential the form could not resend.
/// </remarks>
public sealed partial class ModuleSettingsDialogViewModel(
    AdminClient admin, string settingsHref, string moduleName) : ObservableObject
{
    public string ModuleName => moduleName;

    public ObservableCollection<ModuleSettingEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private bool _loaded;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True after a successful save — the opener reloads the modules list on it.</summary>
    public bool Saved { get; private set; }

    public bool NoSettings => Loaded && Entries.Count == 0;

    public event Action? CloseRequested;

    public async Task LoadAsync()
    {
        try
        {
            foreach (var setting in await admin.GetModuleSettingsAsync(settingsHref))
            {
                Entries.Add(new ModuleSettingEntryViewModel(setting));
            }
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadTenant");
        }

        Loaded = true;
        OnPropertyChanged(nameof(NoSettings));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Status = string.Empty;

        // The what-to-send rule is shared with the web client (ADR 0772): an untouched secret is omitted, an
        // emptied plain field is cleared. Two copies of that answer is how the two forms come to disagree.
        var values = SimplArchive.Presentation.ModuleSettingsForm.ValuesToSend(
            Entries.Select(e => new SimplArchive.Presentation.ModuleSettingsForm.Field(e.Key, e.IsSecret, e.Entry)));

        try
        {
            await admin.SetModuleSettingsAsync(settingsHref, values);
            Saved = true;
            CloseRequested?.Invoke();
        }
        catch (ApiActionException exception)
        {
            Status = exception.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadTenant");
        }
    }
}

/// <summary>One field on the form — the declaration, plus what the user has typed.</summary>
public sealed partial class ModuleSettingEntryViewModel : ObservableObject
{
    public ModuleSettingEntryViewModel(AdminClient.ModuleSettingInfo setting)
    {
        Key = setting.Key;
        Label = setting.Label;
        Description = setting.Description ?? string.Empty;
        IsSecret = setting.IsSecret;
        HasValue = setting.HasValue;

        // A secret starts EMPTY even when set: its value never crossed the wire, so there is nothing to
        // prefill — and the watermark is what says the box being empty does not mean "not configured".
        Entry = setting.IsSecret ? string.Empty : setting.Value ?? string.Empty;
    }

    public string Key { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsSecret { get; }

    /// <summary>Whether a value is already configured — for a secret this is all the administrator learns.</summary>
    public bool HasValue { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>Masks a secret as it is typed; '\0' is Avalonia's "no masking" for everything else.</summary>
    public char PasswordChar => IsSecret ? '\u2022' : '\0';

    /// <summary>The watermark for a set secret, so an empty box reads as "kept", not "missing".</summary>
    public string Watermark => IsSecret && HasValue ? Strings.Get("ModSettingsSecretSet") : string.Empty;

    [ObservableProperty] private string _entry = string.Empty;
}
