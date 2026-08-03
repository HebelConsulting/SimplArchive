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

// Backs the desktop Compare-versions dialog (ADR "Document version comparison") — two version pickers + an inline
// unified diff, plus an optional "Beyond Compare" launch when that tool is installed on the machine.
public sealed partial class CompareVersionsViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private Guid _documentId;

    [ObservableProperty] private string _documentName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _notAvailable;

    public ObservableCollection<VersionOption> Versions { get; } = [];
    public ObservableCollection<DiffLineViewModel> Lines { get; } = [];

    [ObservableProperty] private VersionOption? _fromVersion;
    [ObservableProperty] private VersionOption? _toVersion;

    // Only offered when Beyond Compare is actually installed (a native-client capability).
    public bool BeyondCompareAvailable { get; } = BeyondCompare.IsInstalled;

    // Restore ("Make current") was moved out of the compare dialog (issue #265) — it lives on the Versions dialog.

    public async Task SetupAsync(SimplArchiveApiClient api, Guid documentId, string documentName)
    {
        _api = api;
        _documentId = documentId;
        DocumentName = documentName;

        Versions.Clear();
        foreach (var v in await api.GetVersionsAsync(documentId))
        {
            Versions.Add(new VersionOption(v.Id, v.VersionNumber ?? 0, v.FileExtension, v.DownloadUrl));
        }

        if (Versions.Count >= 2)
        {
            ToVersion = Versions[0];   // newest
            FromVersion = Versions[1]; // penultimate
            await Compare();           // show the default diff (latest vs penultimate) immediately, no click needed
        }
        else
        {
            Status = Strings.Get("StOneVersion");
        }
    }

    [RelayCommand]
    private async Task Compare()
    {
        if (_api is null || FromVersion is null || ToVersion is null || FromVersion.Id == ToVersion.Id)
        {
            return;
        }

        Lines.Clear();
        NotAvailable = false;
        Status = Strings.Get("StComparing");
        try
        {
            var cmp = await _api.GetVersionComparisonAsync(_documentId, FromVersion.Id, ToVersion.Id);
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
        var bytes = await _api!.DownloadVersionBytesAsync(v.DownloadUrl!);
        var path = Path.Combine(Path.GetTempPath(), $"simplarchive-v{v.Number}-{Guid.NewGuid():N}{v.FileExtension}");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}

public sealed record VersionOption(Guid Id, int Number, string FileExtension, string? DownloadUrl)
{
    public string Label => $"v{Number}";
}

// One diff line — the op decides the row background (added green / removed red / unchanged transparent) and the
// leading +/- marker.
public sealed class DiffLineViewModel
{
    public DiffLineViewModel(int op, string text)
    {
        Op = op;
        Display = (op switch { 1 => "+ ", 2 => "- ", _ => "  " }) + text;
    }

    public int Op { get; }
    public string Display { get; }

    public IBrush Background => Op switch
    {
        1 => new SolidColorBrush(Color.FromArgb(40, 76, 175, 80)),
        2 => new SolidColorBrush(Color.FromArgb(40, 244, 67, 54)),
        _ => Brushes.Transparent,
    };
}
