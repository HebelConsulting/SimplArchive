using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One staged intray item: its indicators, and the context menu carrying the actions that used to be a cluster
/// of buttons on every row (#521).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every handler passes THIS ROW as the Tag</b>, which is the entire point of the refactor. A context menu
/// acts on the row it was opened from; the ribbon's matching buttons act on the current selection. Both resolve
/// through <c>MainWindow.IntrayItemFrom</c>, which prefers the Tag and falls back to the selection — so the two
/// scopes are one expression rather than a decision repeated per action, and neither reads the detail pane's
/// asynchronously-loaded state (ADR 0559).
/// </para>
/// <para>
/// The handlers themselves stay on the window: each needs a parent for a modal dialog or the list's selection,
/// so moving them here would mean reaching back for the window anyway — the same shape as
/// <see cref="IntrayRibbon"/>.
/// </para>
/// </remarks>
public partial class IntrayRow : UserControl
{
    public IntrayRow() => AvaloniaXamlLoader.Load(this);

    private void OnOpen(object? sender, RoutedEventArgs e) => Forward(w => w.OnIntrayOpen(Row(), e));

    private void OnSend(object? sender, RoutedEventArgs e) => Forward(w => w.OnIntraySend(Row(), e));

    private void OnMoveToMine(object? sender, RoutedEventArgs e) => Forward(w => w.OnIntrayMoveToMine(Row(), e));

    private void OnFile(object? sender, RoutedEventArgs e) => Forward(w => w.OnIntrayFile(Row(), e));

    private void OnDelete(object? sender, RoutedEventArgs e) => Forward(w => w.OnIntrayDelete(Row(), e));

    // The sender the window sees carries this row as its Tag, whatever was clicked inside the menu — a menu
    // item's own Tag is empty, and without this the action would silently fall back to the SELECTION, acting
    // on a different item than the one the user right-clicked.
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
