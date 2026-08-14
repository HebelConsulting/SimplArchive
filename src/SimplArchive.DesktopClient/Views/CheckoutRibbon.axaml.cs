using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Check-out tab's ribbon — refresh, and the one WebDAV button (#500).
/// </summary>
/// <remarks>
/// <para>
/// Its own control from the start rather than more markup in <c>MainWindow.axaml</c>, which is on the 1000-line
/// standing-debt list: the Inbox's ribbon became <see cref="InboxRibbon"/> only after it had grown a group per
/// feature inside that file (ADR 0577), and the lesson is cheaper to apply than to relearn.
/// </para>
/// <para>
/// <b>The WebDAV handler stays on the window</b>, for the same reason the Inbox's does — it needs a parent for a
/// modal dialog, which only the window has. Refresh needs nothing, so it binds to the view-model directly and
/// has no forwarder here at all.
/// </para>
/// </remarks>
public partial class CheckoutRibbon : UserControl
{
    public CheckoutRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnWebDavTabButton(object? sender, RoutedEventArgs e) => Window()?.OnWebDavTabButton(sender, e);

    // Null in the headless screenshot renders, which host panes without a window — so the forwarder tolerates it
    // rather than throwing where nothing could have been clicked anyway.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
