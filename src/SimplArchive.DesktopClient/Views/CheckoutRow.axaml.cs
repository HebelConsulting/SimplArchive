using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One checked-out document: its indicators, and the context menu carrying the seven actions that used to be
/// a row of buttons (#521).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every entry acts on THIS row.</b> The ribbon's matching buttons act on the selection instead — the same
/// actions, a different scope, which is the distinction the refactor exists to make visible. Neither reads the
/// detail pane's asynchronously-loaded state (ADR 0559).
/// </para>
/// <para>
/// The two compare actions and discard reach the window (they need a parent for a modal dialog); check-in,
/// unlock, edit and extend are commands on the tab's own view-model and are invoked directly with this row as
/// the parameter — so the row, not the selection, is what they receive.
/// </para>
/// </remarks>
public partial class CheckoutRow : UserControl
{
    public CheckoutRow() => AvaloniaXamlLoader.Load(this);

    private CheckoutRowViewModel? Row => DataContext as CheckoutRowViewModel;

    private CheckoutTabViewModel? Tab =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext is MainWindowViewModel vm ? vm.Checkout : null;

    private void OnEdit(object? sender, RoutedEventArgs e) => Invoke(t => t.EditCommand.Execute(Row));

    private void OnCheckIn(object? sender, RoutedEventArgs e) => Invoke(t => t.CheckInCommand.Execute(Row));

    private void OnUnlock(object? sender, RoutedEventArgs e) => Invoke(t => t.UnlockCommand.Execute(Row));

    private void OnExtend(object? sender, RoutedEventArgs e) => Invoke(t => t.ExtendCommand.Execute(Row));

    private void OnCompare(object? sender, RoutedEventArgs e) => Forward(w => w.OnCheckoutCompare(Tagged(), e));

    private void OnBeyondCompare(object? sender, RoutedEventArgs e) => Forward(w => w.OnCheckoutBeyondCompare(Tagged(), e));

    private void OnSortPages(object? sender, RoutedEventArgs e) => Forward(w => w.OnCheckoutSortPages(Tagged(), e));

    private void OnDiscard(object? sender, RoutedEventArgs e) => Forward(w => w.OnCheckoutDiscard(Tagged(), e));

    // The window's handlers read their target from the sender's Tag, and a MenuItem's own Tag is empty — so
    // this row is handed over explicitly. Without it the action would fall back to the selection and act on a
    // different document than the one right-clicked.
    private Control Tagged() => new Border { Tag = DataContext };

    private void Invoke(Action<CheckoutTabViewModel> action)
    {
        if (Tab is { } tab && Row is not null)
        {
            action(tab);
        }
    }

    // Null in the headless screenshot renders, which host panes without a window.
    private void Forward(Action<MainWindow> action)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            action(window);
        }
    }
}
