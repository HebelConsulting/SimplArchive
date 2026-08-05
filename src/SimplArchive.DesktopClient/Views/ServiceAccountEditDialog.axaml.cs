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

    public ServiceAccountEditDialog(string name, bool canExport, bool canImport,
        bool canManageRepositories, bool canManageMasks, bool canManageServiceAccounts)
    {
        InitializeComponent();
        NameBox.Text = name;
        ExportBox.IsChecked = canExport;
        ImportBox.IsChecked = canImport;
        RepositoriesBox.IsChecked = canManageRepositories;
        MasksBox.IsChecked = canManageMasks;
        ServiceAccountsBox.IsChecked = canManageServiceAccounts;
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
