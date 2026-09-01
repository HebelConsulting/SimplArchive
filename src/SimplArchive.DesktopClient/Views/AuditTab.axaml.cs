using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Audit tab's view (#519, tranche 1). The export and purge handlers MOVED here with the markup they
/// serve (the rule #519 sets for every tranche): they need a TopLevel — the file picker and the confirm
/// dialog's owner — and this control can reach one without the window having to know the tab exists.
/// </summary>
public partial class AuditTab : UserControl
{
    public AuditTab() => AvaloniaXamlLoader.Load(this);

    // Export the audit log as NDJSON to a chosen file (ADR "Audit trail export").
    internal void OnExport(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not AuditTabViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export audit log",
            SuggestedFileName = $"audit-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.ndjson",
        });
        if (file is null || vm.ExportAuditBytesAsync() is not { } bytesTask)
        {
            return;
        }

        try
        {
            var bytes = await bytesTask;
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            vm.Report($"Exported the audit log to {file.Path.LocalPath}.");
        }
        catch (Exception ex)
        {
            vm.Report($"Could not export the audit log: {ex.Message}");
        }
    });

    // Purge aged audit events (tenant-admin): confirm, then run the purge (ADR "Desktop audit viewer" over
    // "Audit trail retention and purge").
    internal void OnPurge(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not AuditTabViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var message = $"Permanently delete audit events older than {(vm.AuditRetentionDays == 0 ? "— (retention disabled)" : $"{vm.AuditRetentionDays} days")}? The tamper-evidence chain stays verifiable over the retained events.";
        if (await new ConfirmDialog(message, "Purge").ShowDialog<bool>(owner))
        {
            await vm.PurgeAuditAsync();
        }
    });
}
