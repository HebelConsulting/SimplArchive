using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// A user or group row in the Users & groups admin list (ADR "Users & groups administration tab"). The icon
// distinguishes them: one person for a user, two for a group.
public sealed class PrincipalRowViewModel(bool isGroup, Guid id, string name, bool isActive, SimplArchiveApiClient.SystemRightsData rights, bool mfaEnabled = false)
{
    public bool IsGroup { get; } = isGroup;
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsActive { get; } = isActive;

    // Whether the user has two-factor enabled (ADR "MFA (interactive login, TOTP)") — the pane shows status +
    // an admin reset. Settable so the reset action can flip it in place.
    public bool MfaEnabled { get; set; } = mfaEnabled;

    public SimplArchiveApiClient.SystemRightsData Rights { get; set; } = rights;

    public string IconValue => IsGroup ? "mdi-account-multiple" : "mdi-account";
    public string DisplayName => IsGroup || IsActive ? Name : $"{Name} (inactive)";
}

// One toggle in the rights matrix. IsChecked is two-way bound to a CheckBox; the VM reads them back in order
// to rebuild the SystemRightsData on Save.
public partial class PrincipalRightViewModel(string label, bool isChecked) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty] private bool _isChecked = isChecked;
}
