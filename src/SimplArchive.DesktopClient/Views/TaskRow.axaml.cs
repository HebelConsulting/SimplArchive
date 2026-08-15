using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One pending task's row (#530, tranche 3): the menu entry acts on THIS row — the command lives on
/// MainWindowViewModel and takes the row as its parameter, so it executes with its own DataContext, never with
/// selection or pane state (ADR 0559).
/// </summary>
public partial class TaskRow : UserControl
{
    public TaskRow() => AvaloniaXamlLoader.Load(this);

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is TaskItemViewModel row)
        {
            vm.OpenTaskCommand.Execute(row);
        }
    }
}
