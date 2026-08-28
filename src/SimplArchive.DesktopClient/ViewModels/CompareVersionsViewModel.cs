using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Compare-versions dialog (ADR 0712) — two version pickers + a side-by-side diff of the
// shared TextDiff rows, plus an optional "Beyond Compare" launch when that tool is installed on the machine.
// The comparison NEVER runs on its own (ADR "Explicit compare"): the result area shows a hint until Compare is
// clicked, the button is disabled until two DIFFERENT versions are picked, and changing a picker after a run
// clears the result back to the hint so a stale diff can't be mistaken for the current selection's.
public sealed partial class CompareVersionsViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private Guid _documentId;

    // The collection's advertised `compare` address; the two versions travel as query parameters (issue #416).
    private string? _compareHref;

    [ObservableProperty] private string _documentName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _notAvailable;

    public ObservableCollection<VersionOption> Versions { get; } = [];

    // The rendered side-by-side rows — computed client-side from the two texts the server extracts (ADR 0712).
    [ObservableProperty] private IReadOnlyList<DiffRowViewModel> _rows = [];

    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [ObservableProperty] private VersionOption? _fromVersion;

    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [ObservableProperty] private VersionOption? _toVersion;

    // Shown in the (empty) result area whenever there's nothing compared yet — leaving it blank would read as
    // "these two versions are identical".
    [ObservableProperty] private bool _showHint = true;

    // Only offered when Beyond Compare is actually installed (a native-client capability).
    public bool BeyondCompareAvailable { get; } = BeyondCompare.IsInstalled;

    // Restore ("Make current") was moved out of the compare dialog (issue #265) — it lives on the Versions dialog.

    public async Task SetupAsync(SimplArchiveApiClient api, Guid documentId, string documentName, string versionsHref)
    {
        _api = api;
        _documentId = documentId;
        DocumentName = documentName;

        Versions.Clear();
        var (versions, compareHref) = await api.Versions.GetVersionsWithLinksAsync(versionsHref);
        _compareHref = compareHref;
        foreach (var v in versions)
        {
            Versions.Add(new VersionOption(v.Id, v.VersionNumber ?? 0, v.FileExtension, v.DownloadUrl));
        }

        if (Versions.Count >= 2)
        {
            // Default the pickers to latest-vs-penultimate, but do NOT run the comparison — diffing two versions
            // means fetching and text-extracting both blobs, so the cost stays behind a deliberate click.
            ToVersion = Versions[0];   // newest
            FromVersion = Versions[1]; // penultimate
            ResetToHint();
        }
        else
        {
            ShowHint = false;
            Status = Strings.Get("StOneVersion");
        }
    }

    // Two DIFFERENT versions are needed for a diff — until then the button stays disabled rather than failing
    // silently on click.
    private bool CanCompare() => FromVersion is not null && ToVersion is not null && FromVersion.Id != ToVersion.Id;

    // Changing either picker discards the rendered diff: it belongs to the old pair, and leaving it up would
    // misattribute it to the new selection.
    partial void OnFromVersionChanged(VersionOption? value) => ResetToHint();

    partial void OnToVersionChanged(VersionOption? value) => ResetToHint();

    private void ResetToHint()
    {
        Rows = [];
        NotAvailable = false;
        ShowHint = true;
        Status = Strings.Get("CompareHint");
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task Compare()
    {
        if (_api is null || FromVersion is null || ToVersion is null || FromVersion.Id == ToVersion.Id)
        {
            return;
        }

        Rows = [];
        NotAvailable = false;
        ShowHint = false;
        Status = Strings.Get("StComparing");
        try
        {
            if (_compareHref is null) { return; }

            var cmp = await _api.Versions.GetVersionComparisonAsync(_compareHref, FromVersion.Id, ToVersion.Id);
            if (!cmp.Available)
            {
                NotAvailable = true;
                Status = Strings.Get("StCompareUnavailable");
                return;
            }

            Rows = DiffRowViewModel.Build(cmp.FromText, cmp.ToText);
            Status = string.Format(Strings.Get("StLines"), Rows.Count);
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

        if (_api is null || FromVersion?.DownloadUrl is null || ToVersion?.DownloadUrl is null)
        {
            return;
        }

        Status = Strings.Get("StOpeningBc");
        try
        {
            var f1 = await StageAsync(FromVersion);
            var f2 = await StageAsync(ToVersion);
            Status = BeyondCompare.Launch(f1, f2) ? "" : "Could not launch Beyond Compare.";
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrBeyondCompare"), e.Message);
        }
    }

    private async Task<string> StageAsync(VersionOption v)
    {
        var bytes = await _api!.Versions.DownloadVersionBytesAsync(v.DownloadUrl!);
        var path = Path.Combine(Path.GetTempPath(), $"simplarchive-v{v.Number}-{Guid.NewGuid():N}{v.FileExtension}");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}

public sealed record VersionOption(Guid Id, int Number, string FileExtension, string? DownloadUrl)
{
    public string Label => $"v{Number}";
}

