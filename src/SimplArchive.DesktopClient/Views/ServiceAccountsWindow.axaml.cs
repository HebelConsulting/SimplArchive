using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The desktop Service Accounts manager (ADR 0534) — list / create / edit-rights / rotate-secret / revoke,
// opened from the Users & groups tab and gated on CanManageServiceAccounts. The code-behind orchestrates the
// sub-dialogs (edit / one-time-secret / confirm) with `this` as the owner; the VM holds the state + API calls.
public partial class ServiceAccountsWindow : Window
{
    public ServiceAccountsWindow() : this(null)
    {
    }

    public ServiceAccountsWindow(SimplArchiveApiClient? client)
    {
        InitializeComponent();
        if (client is not null)
        {
            DataContext = new ServiceAccountsViewModel(client);
            Opened += async (_, _) => await Vm!.LoadAsync();
        }
    }

    private ServiceAccountsViewModel? Vm => DataContext as ServiceAccountsViewModel;

    private void OnCreate(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { } vm || string.IsNullOrWhiteSpace(vm.NewName))
        {
            return;
        }

        await RunAsync(vm, async () =>
        {
            var secret = await vm.Client.CreateServiceAccountAsync(vm.NewName.Trim(), vm.NewRights());
            vm.ResetNewForm();
            await vm.LoadAsync();
            await new ServiceAccountSecretDialog(secret.ClientId, secret.ClientSecret).ShowDialog(this);
        });
    });

    private void OnEdit(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { } vm || sender is not Button { Tag: ServiceAccountRowViewModel row })
        {
            return;
        }

        var result = await new ServiceAccountEditDialog(row.Name,
            row.Info.CanExport, row.Info.CanImport, row.Info.CanManageRepositories,
            row.Info.CanManageMasks, row.Info.CanManageServiceAccounts).ShowDialog<ServiceAccountEditDialog.Result?>(this);
        if (result is null)
        {
            return;
        }

        await RunAsync(vm, async () =>
        {
            var rights = new SimplArchiveApiClient.SystemRightsData(
                false, false, false, false, false, false,
                result.CanManageRepositories, result.CanManageMasks, result.CanManageServiceAccounts, false, false,
                result.CanExport, result.CanImport);
            await vm.Client.UpdateServiceAccountAsync(row.Info, result.Name, rights);
            await vm.LoadAsync();
        });
    });

    private void OnRotate(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { } vm || sender is not Button { Tag: ServiceAccountRowViewModel row })
        {
            return;
        }

        await RunAsync(vm, async () =>
        {
            var secret = await vm.Client.RotateServiceAccountSecretAsync(row.Info);
            await new ServiceAccountSecretDialog(secret.ClientId, secret.ClientSecret).ShowDialog(this);
        });
    });

    private void OnRevoke(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { } vm || sender is not Button { Tag: ServiceAccountRowViewModel row })
        {
            return;
        }

        var message = string.Format(Strings.Get("SaRevokeConfirm"), row.Name);
        if (!await new ConfirmDialog(message, Strings.Get("SaRevoke")).ShowDialog<bool>(this))
        {
            return;
        }

        await RunAsync(vm, async () =>
        {
            await vm.Client.RevokeServiceAccountAsync(row.Info);
            await vm.LoadAsync();
        });
    });

    // Run an API action with the busy flag set, turning an expected ApiActionException into a status message
    // (rather than the crash guard's dialog) — the same "surface the message" contract the web dialog uses.
    private static async Task RunAsync(ServiceAccountsViewModel vm, Func<Task> action)
    {
        vm.Busy = true;
        vm.Status = "";
        try
        {
            await action();
        }
        catch (ApiActionException ex)
        {
            vm.Status = ex.Message;
        }
        finally
        {
            vm.Busy = false;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
