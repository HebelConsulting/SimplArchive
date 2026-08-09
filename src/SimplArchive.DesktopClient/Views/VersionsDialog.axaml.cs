using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The Versions modal (ADR "Versions dialog") — a list of a document's versions with Open / Save as / Make
// current + a Compare launcher. Its VM is set as the DataContext by the caller after SetupAsync loads versions.
public partial class VersionsDialog : Window
{
    public VersionsDialog()
    {
        InitializeComponent();
    }

    public VersionsDialog(VersionsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private VersionsViewModel? Vm => DataContext as VersionsViewModel;

    private static VersionRowViewModel? RowOf(object? sender) => (sender as Control)?.DataContext as VersionRowViewModel;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Open a version in its native application (download the bytes to a temp file named with the version's
    // extension, then hand off to the OS).
    private void OnOpen(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { Api: { } api } vm || RowOf(sender) is not { DownloadUrl: { } url } row)
        {
            return;
        }

        var bytes = await api.DownloadVersionBytesAsync(url);
        var fileName = MainWindowViewModel.WithExtension($"{vm.DocumentName} v{row.VersionNumber}", row.FileExtension);
        await NativeFileOpener.OpenBytesAsync(bytes, fileName);
    });

    private void OnSaveAs(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { Api: { } api } vm || RowOf(sender) is not { DownloadUrl: { } url } row)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = MainWindowViewModel.WithExtension($"{vm.DocumentName} v{row.VersionNumber}", row.FileExtension),
        });
        if (file is null)
        {
            return; // cancelled
        }

        var bytes = await api.DownloadVersionBytesAsync(url);
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
    });

    // Make current acts on the single selected (non-current) version, behind a confirmation naming it, so it
    // can't fire by accident (ADR "Deliberate make-current in the Versions dialog"). The restore itself is
    // non-destructive (ADR "Version restore") — it adds a new current version, keeping history.
    private void OnMakeCurrent(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { SelectedVersion: { IsCurrent: false } row } vm)
        {
            return;
        }

        var confirmed = await new ConfirmDialog(
            $"Make v{row.VersionNumber} the current version? A new current version is created; earlier versions are kept.",
            "Make current").ShowDialog<bool>(this);
        if (confirmed)
        {
            await vm.MakeCurrentCommand.ExecuteAsync(row);
        }
    });

    // Compare… launches the existing Compare-versions dialog (ADR "Document version comparison"); a restore there
    // also marks this dialog changed so the parent refreshes, and reloads this list.
    private void OnCompare(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (Vm is not { Api: { } api } vm)
        {
            return;
        }

        var cvm = new CompareVersionsViewModel();
        await cvm.SetupAsync(api, vm.DocumentId, vm.DocumentName, vm.VersionsHref);
        var dialog = new CompareVersionsDialog(cvm);
        await dialog.ShowDialog(this); // compare is read-only now — "Make current" lives on this Versions dialog (#265)
    });
}
