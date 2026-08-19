using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Audit ribbon (#530, tranche 9). Export's save-file dialog and purge's confirm need a TopLevel as
/// their parent, so those handlers live on the enclosing <see cref="AuditTab"/> (#519 moved them there with
/// the tab's markup) and this control only forwards; the verify pair binds to the view-model directly and
/// has no forwarder at all.
/// </summary>
public partial class AuditRibbon : UserControl
{
    public AuditRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnExport(object? sender, RoutedEventArgs e) => Tab()?.OnExport(sender, e);

    private void OnPurge(object? sender, RoutedEventArgs e) => Tab()?.OnPurge(sender, e);

    // Null in a headless render that hosts the ribbon without the tab around it.
    private AuditTab? Tab() => this.FindAncestorOfType<AuditTab>();
}
