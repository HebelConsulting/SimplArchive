using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One catalog-tag row (#530, tranche 6). Every menu entry hands THIS ROW over as the Tag (the RecycleBinRow
/// recipe), so the window's handlers act on the row the menu was opened from, never on the selection
/// (ADR 0559); retire runs the view-model's row command directly.
/// </summary>
public partial class TagRow : UserControl
{
    public TagRow() => AvaloniaXamlLoader.Load(this);

    private void OnRename(object? sender, RoutedEventArgs e) => Forward(w => w.OnRenameTag(Row(), e));

    private void OnRecolour(object? sender, RoutedEventArgs e) => Forward(w => w.OnRecolourTag(Row(), e));

    private void OnMerge(object? sender, RoutedEventArgs e) => Forward(w => w.OnMergeTag(Row(), e));

    private void OnRetire(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm && DataContext is TagCatalogRow row)
        {
            vm.RetireTagCommand.Execute(row);
        }
    }

    // The window's handlers read their target from the sender's Tag — this row is handed over explicitly,
    // or the action would fall back to the selection.
    private Control Row() => new Border { Tag = DataContext };

    // Null in the headless screenshot renders, which host panes without a window.
    private void Forward(Action<MainWindow> action)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            action(window);
        }
    }
}
