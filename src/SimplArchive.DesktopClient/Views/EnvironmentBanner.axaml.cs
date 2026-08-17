using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The environment strip at the top of the main window (#501). All state lives in
/// <see cref="ViewModels.EnvironmentBannerViewModel"/>; its own control because <c>MainWindow.axaml</c> is on
/// the over-limit debt list with four lines of headroom, and the Intray ribbon already set the precedent that
/// a window region is a thing, and things get a file (ADR 0577).
/// </summary>
public partial class EnvironmentBanner : UserControl
{
    public EnvironmentBanner() => AvaloniaXamlLoader.Load(this);
}
