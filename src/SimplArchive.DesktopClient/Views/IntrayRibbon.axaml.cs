using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Intray tab's ribbon — pages, straightening, separator sheets, the item actions and WebDAV.
/// </summary>
/// <remarks>
/// <para>
/// Its own control since ADR 0577. It grew a group per feature (#487 pages, #491 straightening, #492 separator
/// sheets) and each one arrived as more lines in <c>MainWindow.axaml</c>, which is on the 1000-line standing-debt
/// list and had already been raised three times. A ribbon that gains a group per feature is a thing, and things
/// get a file.
/// </para>
/// <para>
/// <b>The handlers stay on the window.</b> Every one of them needs something only the window has — the intray
/// list's selection, or a parent for a modal dialog — so moving them here would mean reaching back for the
/// window anyway, from ten places instead of one. What lives here is the forwarding, and it is deliberately
/// dull: the XAML <c>Click</c> attribute resolves against this control's code-behind, so each button needs a
/// stub whether or not it does anything of its own.
/// </para>
/// <para>
/// The DataContext is inherited, so the bindings are unchanged from when this markup was inline, and so are the
/// AutomationIds the desktop UI tests address.
/// </para>
/// </remarks>
public partial class IntrayRibbon : UserControl
{
    public IntrayRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnIntrayOpen(object? sender, RoutedEventArgs e) => Window()?.OnIntrayOpen(sender, e);

    private void OnIntraySend(object? sender, RoutedEventArgs e) => Window()?.OnIntraySend(sender, e);

    private void OnIntrayMoveToMine(object? sender, RoutedEventArgs e) => Window()?.OnIntrayMoveToMine(sender, e);

    private void OnIntrayFile(object? sender, RoutedEventArgs e) => Window()?.OnIntrayFile(sender, e);

    private void OnIntrayDelete(object? sender, RoutedEventArgs e) => Window()?.OnIntrayDelete(sender, e);

    private void OnIntraySplit(object? sender, RoutedEventArgs e) => Window()?.OnIntraySplit(sender, e);

    private void OnIntraySortPages(object? sender, RoutedEventArgs e) => Window()?.OnIntraySortPages(sender, e);

    private void OnIntrayJoin(object? sender, RoutedEventArgs e) => Window()?.OnIntrayJoin(sender, e);

    private void OnIntrayRotateAutoToggled(object? sender, RoutedEventArgs e) => Window()?.OnIntrayRotateAutoToggled(sender, e);

    private void OnIntrayDeskewAutoToggled(object? sender, RoutedEventArgs e) => Window()?.OnIntrayDeskewAutoToggled(sender, e);

    private void OnIntrayDeskew(object? sender, RoutedEventArgs e) => Window()?.OnIntrayDeskew(sender, e);

    private void OnIntrayPatchAutoToggled(object? sender, RoutedEventArgs e) => Window()?.OnIntrayPatchAutoToggled(sender, e);

    private void OnIntrayPatchCut(object? sender, RoutedEventArgs e) => Window()?.OnIntrayPatchCut(sender, e);

    private void OnIntrayPatchSheet(object? sender, RoutedEventArgs e) => Window()?.OnIntrayPatchSheet(sender, e);

    private void OnIntrayFileMultiple(object? sender, RoutedEventArgs e) => Window()?.OnIntrayFileMultiple(sender, e);

    private void OnWebDavTabButton(object? sender, RoutedEventArgs e) => Window()?.OnWebDavTabButton(sender, e);

    // Null in the headless screenshot renders, which host panes without a window — so every forwarder tolerates
    // it rather than throwing where nothing could have been clicked anyway.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
