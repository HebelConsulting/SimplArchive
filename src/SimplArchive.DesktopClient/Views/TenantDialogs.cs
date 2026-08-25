using System.Threading.Tasks;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Opens the Tenant tab's administration dialogs: the mail domains (#667, ADR 0692) and the sensitivity labels.
/// </summary>
/// <remarks>
/// <para>
/// Their own file rather than one open-a-dialog block per feature in MainWindow's code-behind, which is on the
/// standing size list: each needs the window only as a PARENT, and nothing else about them belongs to it. The
/// mail-domains one was added here and the labels one moved beside it in the same change, so the file this
/// feature touches came out smaller than it went in.
/// </para>
/// <para>
/// The difference between them is the one line worth reading: the labels' catalog feeds a picker the shell
/// holds, so it is reloaded on close. Nothing caches mail domains, so nothing is.
/// </para>
/// </remarks>
internal static class TenantDialogs
{
    public static async Task OpenMailDomainsAsync(MainWindow window, MainWindowViewModel? viewModel)
    {
        if (viewModel?.CreateMailDomainsViewModel() is not { } domains)
        {
            return; // not signed in — there is no tenant to configure
        }

        await domains.LoadAsync();
        await new MailDomainsDialog(domains).ShowDialog(window);
    }

    public static async Task OpenSensitivityLabelsAsync(MainWindow window, MainWindowViewModel? viewModel)
    {
        if (viewModel?.CreateSensitivityLabelsViewModel() is not { } labels)
        {
            return;
        }

        await labels.LoadAsync();
        await new SensitivityLabelsDialog(labels).ShowDialog(window);
        await viewModel.LoadSensitivityCatalogAsync(); // pick up any changes for the picker
    }
}
