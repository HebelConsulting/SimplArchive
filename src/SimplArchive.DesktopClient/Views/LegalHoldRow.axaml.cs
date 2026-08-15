using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One legal-hold row (#530, tranche 5). The window's release handler acts on the selection — the whole
/// detail pane does — so opening this row's menu makes the row the selection first (synchronously, before
/// the handler reads it): the PrincipalRow recipe, ADR 0559 satisfied from the selection side.
/// </summary>
public partial class LegalHoldRow : UserControl
{
    public LegalHoldRow() => AvaloniaXamlLoader.Load(this);

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is LegalHoldRowViewModel row)
        {
            vm.SelectedLegalHold = row;
        }
    }

    private void OnRelease(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.OnReleaseLegalHold(sender, e);
        }
    }
}
