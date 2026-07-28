using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Versions dialog (ADR "Versions dialog"): lists a document's confirmed versions with Open /
// Save as / Make current per row + a Compare launcher. "Make current" reinstates a version as a new current
// version via the non-destructive restore (ADR "Version restore"); the latest is labelled "current".
public sealed partial class VersionsViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private Guid _documentId;

    [ObservableProperty] private string _documentName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasMultiple))] private bool _loaded;

    // The single-selected row (ADR "Deliberate make-current in the Versions dialog"); "Make current" is only
    // offered for a selected, non-current version and behind a confirmation (the confirm lives in the view).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanMakeCurrentSelected))] private VersionRowViewModel? _selectedVersion;
    public bool CanMakeCurrentSelected => SelectedVersion is { CanMakeCurrent: true };

    public ObservableCollection<VersionRowViewModel> Versions { get; } = [];

    // Set by the dialog code-behind; Changed tells the caller to refresh the detail after the dialog closes.
    public Action? RequestClose { get; set; }
    public bool Changed { get; private set; }

    public SimplArchiveApiClient? Api => _api;
    public Guid DocumentId => _documentId;
    public bool HasMultiple => Versions.Count >= 2;

    // Called by the dialog when a restore happens via the Compare launcher, so the caller refreshes the detail.
    public void MarkChanged() => Changed = true;

    public async Task SetupAsync(SimplArchiveApiClient api, Guid documentId, string documentName)
    {
        _api = api;
        _documentId = documentId;
        DocumentName = documentName;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_api is null)
        {
            return;
        }

        Versions.Clear();
        var list = await _api.GetVersionsAsync(_documentId); // confirmed, newest first
        var currentId = list.FirstOrDefault()?.Id;
        foreach (var v in list)
        {
            Versions.Add(new VersionRowViewModel(v.Id, v.VersionNumber ?? 0, v.DocumentDate,
                v.CreatedAt == default ? "" : v.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                v.CreatedByName, v.DownloadUrl, v.FileExtension, v.Id == currentId));
        }

        Loaded = true;
        OnPropertyChanged(nameof(HasMultiple));
    }

    [RelayCommand]
    private async Task MakeCurrent(VersionRowViewModel? row)
    {
        if (row is null || _api is null)
        {
            return;
        }

        Status = Strings.Get("StMakingCurrent");
        try
        {
            await _api.RestoreVersionAsync(_documentId, row.Id);
            Changed = true;
            Status = string.Format(Strings.Get("StVersionCurrent"), row.VersionNumber);
            await ReloadAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }
}

// One row in the Versions dialog.
public sealed record VersionRowViewModel(Guid Id, int VersionNumber, string DocumentDate, string Filed, string By, string? DownloadUrl, string FileExtension, bool IsCurrent)
{
    public string Label => $"v{VersionNumber}";
    public bool CanMakeCurrent => !IsCurrent;
}
