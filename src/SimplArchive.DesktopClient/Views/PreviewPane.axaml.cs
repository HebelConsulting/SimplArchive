using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

// The preview surface (toolbar + pages/text). Reused docked and in the full-screen overlay — see PreviewPane.axaml.
public partial class PreviewPane : UserControl
{
    public PreviewPane() => AvaloniaXamlLoader.Load(this);
}
