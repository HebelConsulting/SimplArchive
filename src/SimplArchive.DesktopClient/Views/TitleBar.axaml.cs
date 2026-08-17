using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The application chrome: the wordmark, the notifications bell, the theme switcher and the account menu.
/// </summary>
/// <remarks>
/// <para>
/// Its own control since ADR 0578, for the same reason as the Intray ribbon: it kept gaining responsibilities —
/// notifications, MFA, passkeys, WebDAV, the profile, and now the light/dark choice — while living in a file
/// already on the 1000-line standing-debt list with no headroom left. Chrome is a responsibility, not a region.
/// </para>
/// <para>
/// <b>The handlers stay on the window.</b> Every one of them opens a modal owned by it, so moving them here
/// would mean reaching back for the window anyway, from nine places instead of one. What lives here is the
/// forwarding, and it is deliberately dull: the XAML <c>Click</c> attribute resolves against this control's
/// code-behind, so each item needs a stub whether or not it does anything of its own.
/// </para>
/// </remarks>
public partial class TitleBar : UserControl
{
    public TitleBar() => AvaloniaXamlLoader.Load(this);

    // The theme choice is the one thing here that needs no window: it is applied to the application and
    // remembered per machine, with no modal to own.
    private void OnThemeSystem(object? sender, RoutedEventArgs e) => Services.ThemeModeService.Save(Services.ThemeMode.System);

    private void OnThemeLight(object? sender, RoutedEventArgs e) => Services.ThemeModeService.Save(Services.ThemeMode.Light);

    private void OnThemeDark(object? sender, RoutedEventArgs e) => Services.ThemeModeService.Save(Services.ThemeMode.Dark);

    private void OnBellClick(object? sender, RoutedEventArgs e) => Window()?.OnBellClick(sender, e);

    private void OnEditProfile(object? sender, RoutedEventArgs e) => Window()?.OnEditProfile(sender, e);

    private void OnSetUpMfa(object? sender, RoutedEventArgs e) => Window()?.OnSetUpMfa(sender, e);

    private void OnDisableMfa(object? sender, RoutedEventArgs e) => Window()?.OnDisableMfa(sender, e);

    private void OnManagePasskeys(object? sender, RoutedEventArgs e) => Window()?.OnManagePasskeys(sender, e);

    private void OnManageWebDav(object? sender, RoutedEventArgs e) => Window()?.OnManageWebDav(sender, e);

    private void OnManageImap(object? sender, RoutedEventArgs e) => Window()?.OnManageImap(sender, e);

    private void OnNotificationPreferences(object? sender, RoutedEventArgs e) => Window()?.OnNotificationPreferences(sender, e);

    private void OnOpenManual(object? sender, RoutedEventArgs e) => Window()?.OnOpenManual(sender, e);

    private void OnShowAbout(object? sender, RoutedEventArgs e) => Window()?.OnShowAbout(sender, e);

    // Null in the headless screenshot renders, which host panes without a window — so every forwarder tolerates
    // it rather than throwing where nothing could have been clicked anyway.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
