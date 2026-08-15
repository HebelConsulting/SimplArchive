using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One retention-schedule row (#530, tranche 2): its columns, and the context menu whose entries act on THIS
/// row — the commands live on MainWindowViewModel and already take the row as their parameter, so the menu
/// executes them directly with its own DataContext, never with pane or selection state (ADR 0559).
/// </summary>
public partial class RetentionRow : UserControl
{
    public RetentionRow() => AvaloniaXamlLoader.Load(this);

    private void OnDispose(object? sender, RoutedEventArgs e) => Execute(vm => vm.DisposeRetentionCommand);

    private void OnExtend(object? sender, RoutedEventArgs e) => Execute(vm => vm.ExtendRetentionCommand);

    // Null in the headless screenshot renders, which host panes without a window.
    private void Execute(Func<MainWindowViewModel, System.Windows.Input.ICommand> command)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is RetentionRowViewModel row)
        {
            command(vm).Execute(row);
        }
    }
}
