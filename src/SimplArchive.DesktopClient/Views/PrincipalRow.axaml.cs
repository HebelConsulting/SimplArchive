using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One principal row (#530, tranche 4). The window's copy/delete handlers act on the selection — the whole
/// rights pane does, and the delete retry flow depends on it — so instead of passing the row around, opening
/// this row's menu makes the row the selection first (synchronously, before any handler reads it), which is
/// ADR 0559 satisfied from the other side: the menu's subject and the selection are the same row by the time
/// either is consulted.
/// </summary>
public partial class PrincipalRow : UserControl
{
    public PrincipalRow() => AvaloniaXamlLoader.Load(this);

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (Vm() is { } vm && DataContext is PrincipalRowViewModel row)
        {
            vm.SelectedPrincipal = row;
        }
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => Window()?.OnCopyPrincipal(sender, e);

    private void OnDelete(object? sender, RoutedEventArgs e) => Window()?.OnDeletePrincipal(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;

    private MainWindowViewModel? Vm() => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
}
