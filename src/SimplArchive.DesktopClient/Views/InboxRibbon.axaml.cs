using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Inbox tab's ribbon — pages, straightening, separator sheets, the item actions and WebDAV.
/// </summary>
/// <remarks>
/// <para>
/// Its own control since ADR 0577. It grew a group per feature (#487 pages, #491 straightening, #492 separator
/// sheets) and each one arrived as more lines in <c>MainWindow.axaml</c>, which is on the 1000-line standing-debt
/// list and had already been raised three times. A ribbon that gains a group per feature is a thing, and things
/// get a file.
/// </para>
/// <para>
/// <b>The handlers stay on the window.</b> Every one of them needs something only the window has — the inbox
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
public partial class InboxRibbon : UserControl
{
    public InboxRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnInboxSplit(object? sender, RoutedEventArgs e) => Window()?.OnInboxSplit(sender, e);

    private void OnInboxSortPages(object? sender, RoutedEventArgs e) => Window()?.OnInboxSortPages(sender, e);

    private void OnInboxJoin(object? sender, RoutedEventArgs e) => Window()?.OnInboxJoin(sender, e);

    private void OnInboxDeskewAutoToggled(object? sender, RoutedEventArgs e) => Window()?.OnInboxDeskewAutoToggled(sender, e);

    private void OnInboxDeskew(object? sender, RoutedEventArgs e) => Window()?.OnInboxDeskew(sender, e);

    private void OnInboxPatchAutoToggled(object? sender, RoutedEventArgs e) => Window()?.OnInboxPatchAutoToggled(sender, e);

    private void OnInboxPatchCut(object? sender, RoutedEventArgs e) => Window()?.OnInboxPatchCut(sender, e);

    private void OnInboxPatchSheet(object? sender, RoutedEventArgs e) => Window()?.OnInboxPatchSheet(sender, e);

    private void OnInboxFileMultiple(object? sender, RoutedEventArgs e) => Window()?.OnInboxFileMultiple(sender, e);

    private void OnWebDavTabButton(object? sender, RoutedEventArgs e) => Window()?.OnWebDavTabButton(sender, e);

    // Null in the headless screenshot renders, which host panes without a window — so every forwarder tolerates
    // it rather than throwing where nothing could have been clicked anyway.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
