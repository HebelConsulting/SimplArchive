using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Modal workflow window, opened on demand from the Repositories ribbon / row context menu (ADR "Workflow
// start on demand"). DataContext is a WorkflowWindowViewModel (created + loaded by the caller); shows the
// document's current workflow status + valid transitions + history.
public partial class WorkflowWindow : Window
{
    public WorkflowWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
