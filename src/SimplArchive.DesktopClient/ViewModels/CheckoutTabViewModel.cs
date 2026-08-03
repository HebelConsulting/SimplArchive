using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Check-out tab (ADR "Document check-out / check-in"; ADR 0513). Editing a checked-out document
// happens through the WebDAV mount, which writes the document's cloud stash — there is no local working copy any
// more (the pre-WebDAV local-folder model was retired, ADR 0513). So each row's "modified" is the SERVER's
// `IsModified` (SHA-256(stash) != version SHA), and Check-in is the stash-based server promotion; the desktop no
// longer downloads/uploads/hashes a local file. Edit opens the file via the WebDAV mount so saves flow to the stash.
public sealed partial class CheckoutTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    // Set by MainWindowViewModel to route messages to the shared bottom status bar.
    public Action<string>? StatusReporter { get; set; }

    // Invoked after a check-in / unlock / discard, so the Repositories list (lock glyphs) + the tab count refresh.
    public Func<Task>? OnChanged { get; set; }

    public void Setup(SimplArchiveApiClient api) => _api = api;

    public ObservableCollection<CheckoutRowViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public int Count => Items.Count;

    [ObservableProperty] private string _status = "";

    private void Report(string message)
    {
        Status = message;
        StatusReporter?.Invoke(message);
    }

    public async Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        Items.Clear();
        try
        {
            foreach (var item in await _api.GetCheckoutsAsync())
            {
                Items.Add(new CheckoutRowViewModel
                {
                    Id = item.Id,
                    Name = item.Name,
                    Path = item.Path,
                    FileExtension = item.FileExtension,
                    IsModified = item.IsModified,
                    ExpiresAt = item.ExpiresAt,
                    StashDownloadUrl = item.StashDownloadUrl,
                });
            }

            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(Count));
            Status = Items.Count == 0 ? "No documents are checked out." : $"{Items.Count} document(s) checked out.";
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
        }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    // Edit: open the checked-out file through the WebDAV mount (ADR 0513) so the native editor's saves flow straight
    // to the cloud stash — after which the next Refresh shows it as Modified and offers Check in. Best-effort: an
    // unconfigured WebDAV password or an unreachable mount is reported, never throws.
    [RelayCommand]
    private async Task Edit(CheckoutRowViewModel? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            var webdav = await _api.GetWebDavStatusAsync();
            if (!webdav.Enabled || string.IsNullOrWhiteSpace(webdav.Url))
            {
                Report(Strings.Get("CoEditNeedsWebDav"));
                return;
            }

            var fileName = MainWindowViewModel.WithExtension(row.Name, row.FileExtension);
            var result = await OsFileManager.OpenWebDavFileAsync(webdav.Url, $"Personal/Check-out/{fileName}");
            Report(result.Success
                ? string.Format(Strings.Get("CoEditing"), row.Name)
                : result.Error ?? $"Could not open '{row.Name}'.");
        }
        catch (Exception e)
        {
            Report($"Could not open '{row.Name}' for editing: {e.Message}");
        }
    }

    // Check in: the server promotes the cloud stash (the WebDAV-edited working copy) to a new confirmed version and
    // releases the lock (ADR 0513). Only offered when the row is Modified.
    [RelayCommand]
    private async Task CheckIn(CheckoutRowViewModel? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.CheckInFromStashAsync(row.Id);
            Report($"Checked in '{row.Name}'.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not check in '{row.Name}': {e.Message}");
        }
    }

    // Extend: reset the auto-release idle timer (ADR "Self-service check-out extension") — keeps the lock, no
    // version, no stash change.
    [RelayCommand]
    private async Task Extend(CheckoutRowViewModel? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.ExtendCheckoutAsync(row.Id);
            Report($"Extended the check-out of '{row.Name}'.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not extend '{row.Name}': {e.Message}");
        }
    }

    // Unlock: nothing to commit — release the lock (the server-side release also clears the stash).
    [RelayCommand]
    private Task Unlock(CheckoutRowViewModel? row) => ReleaseAsync(row, discard: false);

    // Discard: abandon the working copy — release the lock (which drops the stash) without a new version. The
    // confirmation dialog lives in the code-behind (data loss).
    public Task DiscardAsync(CheckoutRowViewModel row) => ReleaseAsync(row, discard: true);

    private async Task ReleaseAsync(CheckoutRowViewModel? row, bool discard)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.CheckInAsync(row.Id); // DELETE the check-out — releases the lock + clears the stash server-side
            Report(discard ? $"Discarded the check-out of '{row.Name}'." : $"Released '{row.Name}'.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not release '{row.Name}': {e.Message}");
        }
    }

    private async Task ReloadAllAsync()
    {
        await LoadAsync();
        if (OnChanged is not null)
        {
            await OnChanged();
        }
    }

    // Populates the Check-out tab for the headless screenshot (no network): one modified (Edit / Check in / Discard)
    // and one unchanged (Edit / Unlock).
    internal void PopulateDemoForScreenshot()
    {
        Items.Clear();
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Contract draft", Path = "Repositories / Demo Repository / Contracts", FileExtension = ".docx", IsModified = true });
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Quarterly report", Path = "Repositories / Demo Repository / Finance", FileExtension = ".xlsx", IsModified = false });
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Count));
        Status = "2 document(s) checked out.";
    }
}

// One row in the Check-out tab: a checked-out document + its server-computed modification state (ADR 0513).
public sealed class CheckoutRowViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string FileExtension { get; init; }

    // The working copy in check-out (the cloud stash) differs from the current version — computed server-side.
    public required bool IsModified { get; init; }

    // Presigned GET for the working-copy stash (ADR 0517) — staged as the right-hand file for Beyond Compare.
    public string? StashDownloadUrl { get; init; }

    // Name shown in the list, WITH extension (ADR 0513): the archive stores a bare stem.
    public string DisplayName => Name + FileExtension;

    // When an idle check-out will be auto-released (ADR "Check-out expiry UX"); null when disabled.
    public DateTimeOffset? ExpiresAt { get; init; }
    public string ExpiresText => ExpiresAt is { } e
        ? e.LocalDateTime.ToString("g") + ((e - DateTimeOffset.UtcNow).TotalDays <= 1 ? " (soon)" : "")
        : "Never";

    public bool CanCheckIn => IsModified;
    public bool CanDiscard => IsModified;
    public bool CanUnlock => !IsModified; // unchanged — release without a new version
    public bool CanExtend => ExpiresAt is not null; // only meaningful when auto-release is enabled

    public string StatusText => IsModified ? Strings.Get("CoModified") : Strings.Get("CoUnchanged");
}
