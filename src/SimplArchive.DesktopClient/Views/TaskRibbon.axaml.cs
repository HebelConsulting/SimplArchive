using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Tasks tab's ribbon (#530, tranche 3): Open acting on the selected row, and refresh. Pure markup —
/// both commands live on MainWindowViewModel and bind directly, so there is nothing to forward here.
/// </summary>
public partial class TaskRibbon : UserControl
{
    public TaskRibbon() => AvaloniaXamlLoader.Load(this);
}
