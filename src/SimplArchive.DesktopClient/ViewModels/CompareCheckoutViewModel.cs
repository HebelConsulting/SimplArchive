using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Compare-checkout dialog (ADR 0517) — an inline unified diff of a checked-out document's current
// version vs its working copy in check-out (the cloud stash), plus an optional "Beyond Compare" launch when that
// tool is installed. Fixed two sides (no version pickers), mirroring CompareVersionsViewModel.
public sealed partial class CompareCheckoutViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private Guid _documentId;
    private string _fileExtension = "";
    private string? _stashDownloadUrl;

    [ObservableProperty] private string _documentName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _notAvailable;

    public ObservableCollection<DiffLineViewModel> Lines { get; } = [];

    // Only offered when Beyond Compare is actually installed (a native-client capability).
    public bool BeyondCompareAvailable { get; } = BeyondCompare.IsInstalled;

    public async Task SetupAsync(SimplArchiveApiClient api, Guid documentId, string documentName, string fileExtension, string? stashDownloadUrl)
    {
        _api = api;
        _documentId = documentId;
        _fileExtension = fileExtension;
        _stashDownloadUrl = stashDownloadUrl;
        DocumentName = documentName;

        Lines.Clear();
        NotAvailable = false;
        Status = Strings.Get("StComparing");
        try
        {
            var cmp = await api.GetCheckoutComparisonAsync(documentId);
            if (!cmp.Available)
            {
                NotAvailable = true;
                Status = Strings.Get("StCompareUnavailable");
                return;
            }

            foreach (var l in cmp.Lines)
            {
                Lines.Add(new DiffLineViewModel(l.Op, l.Text));
            }

            Status = string.Format(Strings.Get("StLines"), cmp.Lines.Count);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrCompare"), e.Message);
        }
    }

    [RelayCommand]
    private async Task OpenInBeyondCompare()
    {
        // The button is always shown (ADR 0518) — if Beyond Compare isn't installed, send the user to the vendor.
        if (!BeyondCompareAvailable)
        {
            SystemBrowser.Open("https://www.scootersoftware.com");
            return;
        }

        if (_api is null || _stashDownloadUrl is null)
        {
            return;
        }

        Status = Strings.Get("StOpeningBc");
        try
        {
            // Left: the current confirmed version (what the server diffs against). Right: the working-copy stash.
            var current = await StageAsync(await _api.DownloadCurrentVersionAsync(_documentId), "current");
            var working = await StageAsync(await _api.DownloadStashAsync(_stashDownloadUrl), "working");
            Status = BeyondCompare.Launch(current, working) ? "" : "Could not launch Beyond Compare.";
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrBeyondCompare"), e.Message);
        }
    }

    private async Task<string> StageAsync(byte[] bytes, string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"simplarchive-{label}-{Guid.NewGuid():N}{_fileExtension}");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
