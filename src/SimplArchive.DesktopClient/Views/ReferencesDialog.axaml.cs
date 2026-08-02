using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// Modal showing an item's real primary location plus the folders that reference it (with full paths). Clicking a
// row's Open closes the dialog returning that folder to navigate to; "Make primary" confirms, then closes
// returning a promote result the main window applies. See ADR "References-of-an-item list" and ADR 0506.
public partial class ReferencesDialog : Window
{
    public ReferencesDialog()
    {
        InitializeComponent();
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReferencingFolderViewModel folder })
        {
            Close(new ReferencesDialogResult(folder.Id, Promote: false));
        }
    }

    private void OnOpenPrimary(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReferencesViewModel { PrimaryLocation: { } primary })
        {
            Close(new ReferencesDialogResult(primary.Id, Promote: false));
        }
    }

    // Confirm, then close returning a promote result — the main window performs the atomic move (ADR 0506).
    private void OnMakePrimary(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (sender is not Control { DataContext: ReferencingFolderViewModel folder }
            || DataContext is not ReferencesViewModel { PrimaryLocation: { } primary })
        {
            return;
        }

        var message = string.Format(Strings.Get("RefMakePrimaryConfirm"), folder.Name, primary.Name);
        if (await new ConfirmDialog(message, Strings.Get("RefMakePrimary")).ShowDialog<bool>(this))
        {
            Close(new ReferencesDialogResult(folder.Id, Promote: true));
        }
    });

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
