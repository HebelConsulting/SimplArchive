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
        IsBoolean = string.Equals(setting.Kind, "Boolean", StringComparison.Ordinal);
        IsChoice = string.Equals(setting.Kind, "Choice", StringComparison.Ordinal);

        // The empty option FIRST and always: it is how the form expresses "no value" — clearing the setting so
        // the module falls back to whatever it does without one — and it is also what a stored value the module
        // no longer declares lands on. It carries a LABEL rather than being a blank row, which would read as a
        // list that failed to load.
        Choices = IsChoice
            ?
            [
                new ModuleSettingChoice(string.Empty, Strings.Get("ModSettingsNotSet")),
                .. (setting.Choices ?? []).Select(c => new ModuleSettingChoice(c, c)),
            ]
            : [];

        // A secret starts EMPTY even when set: its value never crossed the wire, so there is nothing to
        // prefill — and the watermark is what says the box being empty does not mean "not configured".
        // A Boolean has no such state: an absent row IS false, so it starts unchecked and saves either way.
        // A Choice starts on its stored value only while the module still declares it (the shared rule —
        // dropping a stale value is a decision both clients must make identically).
        Entry = setting.Kind switch
        {
            "Boolean" => string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
            "Choice" => SimplArchive.Presentation.ModuleSettingsForm.ChoiceEntry(
                setting.Choices ?? [], setting.Value),
            _ => setting.IsSecret ? string.Empty : setting.Value ?? string.Empty,
        };
    }

    public string Key { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsSecret { get; }

    /// <summary>A yes/no renders as a CHECKBOX, not a box you type "true" into — the server refuses anything
    /// else, so a text field would hand the administrator a way to fail the save (ABI 0.27).</summary>
    public bool IsBoolean { get; }

    /// <summary>One decision, so one control offering the declared values — an administrator cannot type a
    /// combination that means nothing and learn it was refused only on save (ABI 0.29).</summary>
    public bool IsChoice { get; }

    /// <summary>What the chooser offers, the empty "no value" option first.</summary>
    public IReadOnlyList<ModuleSettingChoice> Choices { get; }

    /// <summary>The chooser's two-way face over the string the form actually sends — the same arrangement as
    /// <see cref="Checked"/>, and for the same reason: what travels is the module's own value, verbatim.</summary>
    public ModuleSettingChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => string.Equals(c.Value, Entry, StringComparison.Ordinal));
        set => Entry = value?.Value ?? string.Empty;
    }

    /// <summary>The text form's visibility — the three are mutually exclusive, and expressing the negation here
    /// keeps the template free of a converter that only this one screen would use.</summary>
    public bool IsText => !IsBoolean && !IsChoice;

    /// <summary>The checkbox's two-way face over the string the form actually sends.</summary>
    public bool Checked
    {
        get => string.Equals(Entry, "true", StringComparison.OrdinalIgnoreCase);
        set => Entry = value ? "true" : "false";
    }

    /// <summary>Whether a value is already configured — for a secret this is all the administrator learns.</summary>
    public bool HasValue { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>Masks a secret as it is typed; '\0' is Avalonia's "no masking" for everything else.</summary>
    public char PasswordChar => IsSecret ? '\u2022' : '\0';

    /// <summary>The watermark for a set secret, so an empty box reads as "kept", not "missing".</summary>
    public string Watermark => IsSecret && HasValue ? Strings.Get("ModSettingsSecretSet") : string.Empty;

    [ObservableProperty] private string _entry = string.Empty;

    /// <summary>Raised from the hook that actually changes the value <see cref="Checked"/> is computed FROM.
    /// A computed property notified from the wrong hook — or from none — is a control that silently stops
    /// tracking the state it draws.</summary>
    partial void OnEntryChanged(string value)
    {
        OnPropertyChanged(nameof(Checked));
        OnPropertyChanged(nameof(SelectedChoice));
    }
}

/// <summary>One option a <c>Choice</c> setting offers: the value the module stores, and what the form shows for
/// it. The two differ only for the empty "not set" option — a module's values are its own vocabulary and are
/// never translated here (ADR 0767 leaves that to the module).</summary>
public sealed record ModuleSettingChoice(string Value, string Display);
