using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The administration handlers of the workbench window (issue #466 split the code-behind by feature family):
// principals (create/copy/delete, photo, password), MFA + passkeys, impersonation, service accounts, the
// caller's own profile, and the notification bell. Same class — view-glue whose logic lives in the view
// models; the partial split keeps each family reviewable without changing a single call.
public partial class MainWindow
{
    // Users & groups admin (ADR "Users & groups administration tab") — the New/Copy dialogs and the Delete
    // confirm live in the view; the VM does the Api work.
    private void OnNewUser(object? sender, RoutedEventArgs e) => Safe.Fire(() => NewPrincipalAsync(false));

    private void OnNewGroup(object? sender, RoutedEventArgs e) => Safe.Fire(() => NewPrincipalAsync(true));

    private async Task NewPrincipalAsync(bool isGroup)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var result = await new PrincipalDialog(isGroup, "", "").ShowDialog<PrincipalDialog.Result?>(this);
        if (result is not null)
        {
            await vm.CreatePrincipalAsync(isGroup, result.Name, result.Email, null);
        }
    }

    // Copy = the New dialog pre-filled from the selection; the created principal gets the source's rights.
    private void OnCopyPrincipal(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { } p)
        {
            return;
        }

        var initialName = p.IsGroup ? $"{p.Name} (copy)" : p.Name;
        var result = await new PrincipalDialog(p.IsGroup, initialName, "").ShowDialog<PrincipalDialog.Result?>(this);
        if (result is not null)
        {
            await vm.CreatePrincipalAsync(p.IsGroup, result.Name, result.Email, p.Rights);
        }
    });

    private void OnDeletePrincipal(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { } p)
        {
            return;
        }

        var message = p.IsGroup ? $"Delete the group '{p.Name}'?" : $"Deactivate the user '{p.Name}'?";
        var confirmLabel = p.IsGroup ? "Delete" : "Deactivate";
        if (!await new ConfirmDialog(message, confirmLabel).ShowDialog<bool>(this))
        {
            return;
        }

        // A user with pending review tasks can't be deactivated without handing them over (ADR "Workflow
        // review reassignment") — prompt for a replacement reviewer and retry.
        if (await vm.DeleteSelectedPrincipalAsync() == MainWindowViewModel.DeletePrincipalOutcome.NeedsReplacementReviewer)
        {
            var candidates = vm.ReplacementReviewerCandidates();
            if (candidates.Count == 0)
            {
                return;
            }

            if (await new ReplacementReviewerDialog(p.Name, candidates).ShowDialog<Guid?>(this) is { } replacementId)
            {
                await vm.ReassignReviewsAndDeactivateAsync(replacementId);
            }
        }
    });

    // Service accounts (machine-to-machine, ADR 0534) — a self-contained manager window that talks to the API
    // via the shared client; gated on CanManageServiceAccounts (the server enforces it on every call too).
    private void OnManageServiceAccounts(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new ServiceAccountsWindow(api).ShowDialog(this);
        }
    });

    // Profile photo (ADR "User profile photo") — the crop dialog lives in the view; the VM uploads.
    // "Edit profile…" (#464) — replaces the separate photo and password entries. The dialog applies a password
    // change itself; a new photo comes back as bytes and is uploaded here, exactly as ProfilePhotoDialog's did.
    internal void OnEditProfile(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api } vm
            && await new EditProfileDialog(api).ShowDialog<byte[]?>(this) is { } png)
        {
            await vm.SetMyPhotoAsync(png);
        }
    });

    private void OnChangePrincipalPhoto(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && await new ProfilePhotoDialog().ShowDialog<byte[]?>(this) is { } png)
        {
            await vm.SetSelectedUserPhotoAsync(png);
        }
    });

    private void OnRemovePrincipalPhoto(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.RemoveSelectedUserPhotoAsync();
        }
    });

    // Passwords (ADR "User password management") — the dialogs live in the view; the VM does the API call.
    private void OnResetPrincipalPassword(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        var message = $"Reset the password for '{p.Name}'? A new random password will be generated and shown once.";
        if (!await new ConfirmDialog(message, "Reset").ShowDialog<bool>(this))
        {
            return;
        }

        if (await vm.ResetSelectedUserPasswordAsync() is { } password)
        {
            await new GeneratedPasswordDialog(p.Name, password).ShowDialog(this);
        }
    });

    internal void OnSetUpMfa(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api } vm && await new MfaSetupDialog(api).ShowDialog<bool>(this))
        {
            vm.MarkMfaEnabled();
        }
    });

    internal void OnDisableMfa(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (await new ConfirmDialog("You'll no longer be asked for a code when you sign in. Continue?", "Disable").ShowDialog<bool>(this))
        {
            await vm.DisableMyMfaAsync();
        }
    });

    // Passkeys (ADR "Desktop passkey management") — list/remove natively; adding opens the browser ceremony.
    internal void OnManagePasskeys(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new PasskeysDialog(api).ShowDialog(this);
        }
    });

    internal void OnNotificationPreferences(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel { Api: { } api })
        {
            await new NotificationPreferencesDialog(api).ShowDialog(this);
        }
    });

    // Refresh the notifications when the bell opens (ADR "Notification viewer + click-through"); the flyout opens
    // automatically via Button.Flyout.
    internal void OnBellClick(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.LoadNotificationsAsync();
        }
    });

    // Impersonate the selected user (ADR "User impersonation"): swap the session to act as them.
    private void OnImpersonatePrincipal(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedPrincipal is { IsGroup: false } p)
        {
            await vm.ImpersonateAsync(p.Id);
        }
    });

    private void OnResetPrincipalMfa(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        var message = $"Disable two-factor authentication for '{p.Name}'? They'll be able to sign in with just their password until they re-enroll.";
        if (await new ConfirmDialog(message, "Reset").ShowDialog<bool>(this))
        {
            await vm.ResetSelectedUserMfaAsync();
        }
    });
}
