using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Tasks tab's sortable, filterable list (#550) — markup-only; all state lives on the window VM.</summary>
public partial class TasksList : UserControl
{
    public TasksList() => AvaloniaXamlLoader.Load(this);
}
