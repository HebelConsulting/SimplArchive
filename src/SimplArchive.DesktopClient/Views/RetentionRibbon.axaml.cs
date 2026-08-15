using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Retention tab's ribbon (#530, tranche 2). Everything binds to the view-model directly — no dialogs are
/// launched from here (extend's date prompt rides the command's own dialog hook), so no window forwarders.
/// </summary>
public partial class RetentionRibbon : UserControl
{
    public RetentionRibbon() => AvaloniaXamlLoader.Load(this);
}
