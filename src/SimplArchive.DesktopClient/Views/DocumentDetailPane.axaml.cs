using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Repositories tab's detail pane (#519 tranche 8) — index data over (preview | chat).</summary>
/// <remarks>
/// AvaloniaXamlLoader.Load is correct here, unlike the tree and contents panes: this control has no x:Name at
/// all, so there are no generated fields to leave null.
/// </remarks>
public partial class DocumentDetailPane : UserControl
{
    public DocumentDetailPane() => AvaloniaXamlLoader.Load(this);

    private void OnWorkflowTransition(object? sender, RoutedEventArgs e) => Window()?.OnWorkflowTransition(sender, e);

    private void OnEditOcrLanguages(object? sender, RoutedEventArgs e) => Window()?.OnEditOcrLanguages(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
