using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One held document's row (review finding): the menu entries act on THIS row — the commands live on
/// MainWindowViewModel and take the row as their parameter, so they execute with its own DataContext, never
/// with selection or pane state (ADR 0559). Opening the menu also makes the row the selection, so the
/// ribbon's Go to and the menu agree on the subject.
/// </summary>
public partial class LegalHoldItemRow : UserControl
{
    public LegalHoldItemRow() => AvaloniaXamlLoader.Load(this);

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is LegalHoldItemRowViewModel row)
        {
            vm.SelectedHoldItem = row;
        }
    }

    private void OnGoTo(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is LegalHoldItemRowViewModel row)
        {
            vm.GoToHoldItemCommand.Execute(row);
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is LegalHoldItemRowViewModel row)
        {
            vm.RemoveHoldItemCommand.Execute(row);
        }
    }
}
