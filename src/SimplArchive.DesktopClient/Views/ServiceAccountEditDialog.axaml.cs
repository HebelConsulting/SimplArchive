using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Edit an existing service account's name + rights (ADR 0534). ShowDialog<ServiceAccountEditDialog.Result?>
// returns the intended name + the five grantable rights, or null if cancelled. The API caps rights the caller
// can't grant (403); this dialog just collects the intended state. Also used to seed the create form.
public partial class ServiceAccountEditDialog : Window
{
    public ServiceAccountEditDialog() : this("", false, false, false, false, false)
    {
    }

    /// <summary>
    /// The rights editor, capped at what the caller may actually confer (#864).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to offer all five uncapped, and the code-behind said so: "the API caps… this dialog just
    /// collects". The server answers a violation with 403 INSUFFICIENT_RIGHTS_TO_GRANT, so the dialog promised
    /// something the save then refused — ADR 0543's broken promise, and the one finding in this epic that could
    /// NOT be fixed client-side, because no cap was advertised at all until now.
    /// </para>
    /// <para>
    /// A right the caller cannot grant is DISABLED rather than hidden, unlike a missing rel elsewhere: the
    /// checkbox still has to show the account's CURRENT value. Hiding it would misreport a right the account
    /// holds as absent, which is the lying-state failure of ADR 0724 in a different costume.
    /// </para>
    /// </remarks>
    public ServiceAccountEditDialog(string name, bool canExport, bool canImport,
        bool canManageRepositories, bool canManageMasks, bool canManageServiceAccounts,
        Services.AdminClient.GrantableServiceAccountRights? grantable = null)
    {
        InitializeComponent();
        NameBox.Text = name;
        ExportBox.IsChecked = canExport;
        ImportBox.IsChecked = canImport;
        RepositoriesBox.IsChecked = canManageRepositories;
        MasksBox.IsChecked = canManageMasks;
        ServiceAccountsBox.IsChecked = canManageServiceAccounts;

        // Null means the caller did not supply the cap (the design-time ctor, and any caller not yet updated):
        // leave the editor as it was rather than silently disabling everything, which would read as "you may
        // change nothing" and be just as wrong in the other direction.
        if (grantable is { } cap)
        {
            ExportBox.IsEnabled = cap.CanExport;
            ImportBox.IsEnabled = cap.CanImport;
            RepositoriesBox.IsEnabled = cap.CanManageRepositories;
            MasksBox.IsEnabled = cap.CanManageMasks;
            ServiceAccountsBox.IsEnabled = cap.CanManageServiceAccounts;
        }

        Opened += (_, _) => NameBox.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        Close(new Result(name,
            ExportBox.IsChecked == true, ImportBox.IsChecked == true,
            RepositoriesBox.IsChecked == true, MasksBox.IsChecked == true, ServiceAccountsBox.IsChecked == true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    public sealed record Result(string Name, bool CanExport, bool CanImport,
        bool CanManageRepositories, bool CanManageMasks, bool CanManageServiceAccounts);
}
