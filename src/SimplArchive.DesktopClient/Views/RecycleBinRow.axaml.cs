using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One deleted entry: its columns, and the context menu carrying the actions that used to be per-row buttons
/// (#530, tranche 1). Every handler passes THIS ROW as the Tag — the CheckoutRow recipe: a context menu acts on
/// the row it was opened from, the ribbon's twins on the selection, both resolved by the window's
/// RecycleBinRowFrom, and neither reads the detail pane's asynchronously-loaded state (ADR 0559).
/// </summary>
public partial class RecycleBinRow : UserControl
{
    public RecycleBinRow() => AvaloniaXamlLoader.Load(this);

    private void OnRestore(object? sender, RoutedEventArgs e) => Forward(w => w.OnRecycleBinRestore(Row(), e));

    private void OnHardDelete(object? sender, RoutedEventArgs e) => Forward(w => w.OnRecycleBinHardDelete(Row(), e));

    // The window's handlers read their target from the sender's Tag, and a MenuItem's own Tag is empty — so
    // this row is handed over explicitly, or the action would fall back to the selection.
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
