using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Check-out tab (ADR "Document check-out / check-in", stash + exit guard + orphan handling
// "Check-out working-copy stash" / "Web check-out + orphaned local copy").
//
// Two comparisons drive each row:
//  * local vs the repository version (item.Sha256) — "is there something to commit" → Check in / Unlock.
//  * local vs the SYNCED content — "are there edits not backed up to the cloud stash" → Save to cloud + the
//    logout/close exit guard. The synced hash (the SHA-256 of whatever is safely stored — the S3 stash if one
//    exists, else the checked-out original) is tracked in a small PERSISTED manifest in the checkout folder, so
//    it survives a client restart and lets an ORPHANED working copy be detected (a check-out that was checked in
//    from the web, or force-released by an override, while this client held a local copy).
public sealed partial class CheckoutTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private LocalFolders? _localFolders;
    private string? _manifestPath;

    // documentId -> (working-copy filename, synced SHA-256). Persisted to the checkout folder.
    private Dictionary<Guid, ManifestEntry> _manifest = [];

    private sealed record ManifestEntry(string FileName, string SyncedSha);

    // Set by MainWindowViewModel to route messages to the shared bottom status bar.
    public Action<string>? StatusReporter { get; set; }

    // Invoked after a check-in / unlock / discard, so the Repositories list (lock glyphs) + the tab count refresh.
    public Func<Task>? OnChanged { get; set; }

    public void Setup(SimplArchiveApiClient api, LocalFolders localFolders)
    {
        _api = api;
        _localFolders = localFolders;
        _manifestPath = Path.Combine(localFolders.CheckoutDirectory, LocalFolders.CheckoutManifestFileName);
        LoadManifest();
    }

    public ObservableCollection<CheckoutRowViewModel> Items { get; } = [];

    // Orphaned working copies — check-outs that were released elsewhere (a web check-in / an admin override) while
    // this client still holds a local copy. Resolved with Add as new version / Discard.
    public ObservableCollection<OrphanRowViewModel> Orphans { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasOrphans => Orphans.Count > 0;

    public int Count => Items.Count;

    [ObservableProperty] private string _status = "";

    private void Report(string message)
    {
        Status = message;
        StatusReporter?.Invoke(message);
    }

    public async Task LoadAsync()
    {
        if (_api is null || _localFolders is null)
        {
            return;
        }

        Items.Clear();
        try
        {
            foreach (var item in await _api.GetCheckoutsAsync())
            {
                var fileName = MainWindowViewModel.WithExtension(item.Name, item.FileExtension);
                var localPath = _localFolders.CheckoutFilePath(fileName);
                Items.Add(new CheckoutRowViewModel
                {
                    Id = item.Id,
                    Name = item.Name,
                    Path = item.Path,
                    FileExtension = item.FileExtension,
                    RepoSha256 = item.Sha256,
                    LocalPath = localPath,
                    LocalSha256 = ComputeSha256(localPath),
                    // Baseline for "backed up": the manifest's synced sha, else the repo version (conservative).
                    SyncedSha256 = _manifest.TryGetValue(item.Id, out var e) ? e.SyncedSha : item.Sha256,
                    ExpiresAt = item.ExpiresAt,
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

    // On login: restore each check-out's working copy into the local folder — from the cloud stash if one exists,
    // else the current repository version — and record the synced sha. A local file that already exists and
    // differs from the stash (a crash bypassed the exit guard, or the doc was edited on another machine) is KEPT,
    // never silently overwritten. Manifest entries whose document is no longer checked out are ORPHANS (checked
    // in from the web, or force-released) — surfaced for Add as new version / Discard.
    public async Task ReconcileOnLoginAsync()
    {
        if (_api is null || _localFolders is null)
        {
            return;
        }

        try
        {
            var current = await _api.GetCheckoutsAsync();
            var currentIds = current.Select(c => c.Id).ToHashSet();

            foreach (var item in current)
            {
                var fileName = MainWindowViewModel.WithExtension(item.Name, item.FileExtension);
                var localPath = _localFolders.CheckoutFilePath(fileName);
                var localSha = ComputeSha256(localPath);

                if (item is { HasStash: true, StashDownloadUrl: { } url })
                {
                    var stashBytes = await _api.DownloadStashAsync(url);
                    if (localSha is null)
                    {
                        await File.WriteAllBytesAsync(localPath, stashBytes); // restore the in-progress copy
                    }

                    SetSynced(item.Id, fileName, Sha256Of(stashBytes)); // cloud is authoritative; a divergent local flags
                }
                else if (localSha is null)
                {
                    var repoBytes = await _api.DownloadCurrentVersionAsync(item.Id);
                    await File.WriteAllBytesAsync(localPath, repoBytes);
                    SetSynced(item.Id, fileName, Sha256Of(repoBytes));
                }
                else
                {
                    SetSynced(item.Id, fileName, item.Sha256); // no stash: the repo version is the backed-up baseline
                }
            }

            // Orphans: a manifest entry whose document is no longer checked out by me + a local file still present.
            Orphans.Clear();
            foreach (var (docId, entry) in _manifest.Where(kv => !currentIds.Contains(kv.Key)).ToList())
            {
                var localPath = _localFolders.CheckoutFilePath(entry.FileName);
                if (File.Exists(localPath))
                {
                    Orphans.Add(new OrphanRowViewModel
                    {
                        Id = docId,
                        Name = Path.GetFileNameWithoutExtension(entry.FileName),
                        FileExtension = Path.GetExtension(entry.FileName),
                        LocalPath = localPath,
                    });
                }
                else
                {
                    RemoveSynced(docId); // no local file — nothing to resolve, drop the stale entry
                }
            }

            OnPropertyChanged(nameof(HasOrphans));
        }
        catch (Exception)
        {
            // Best-effort — the tab still works; a failed restore just shows the row without a local file.
        }

        await LoadAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    // Downloads the current version into the local checkout folder (called from the Repositories check-out action)
    // and records it as the synced (backed-up) baseline.
    public async Task<bool> DownloadWorkingCopyAsync(Guid documentId, string name, string fileExtension)
    {
        if (_api is null || _localFolders is null)
        {
            return false;
        }

        var bytes = await _api.DownloadCurrentVersionAsync(documentId);
        var fileName = MainWindowViewModel.WithExtension(name, fileExtension);
        await File.WriteAllBytesAsync(_localFolders.CheckoutFilePath(fileName), bytes);
        SetSynced(documentId, fileName, Sha256Of(bytes));
        return true;
    }

    // Save to cloud: upload the working copy to the S3 stash so it survives logout/close (keeping the local file
    // for continued editing). Updates the synced baseline so the row is no longer "un-saved".
    [RelayCommand]
    private async Task SaveToCloud(CheckoutRowViewModel? row)
    {
        if (_api is null || row is null || !File.Exists(row.LocalPath))
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(row.LocalPath);
            await _api.SaveWorkingCopyAsync(row.Id, bytes);
            SetSynced(row.Id, Path.GetFileName(row.LocalPath), Sha256Of(bytes));
            Report($"Saved '{row.Name}' to the cloud.");
            await LoadAsync(); // recompute status; the local file stays for continued editing
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not save '{row.Name}' to the cloud: {e.Message}");
        }
    }

    // Check in: upload the edited local file as a new version, release the lock (which also clears the cloud
    // stash server-side), remove the working copy.
    [RelayCommand]
    private async Task CheckIn(CheckoutRowViewModel? row)
    {
        if (_api is null || row is null || !File.Exists(row.LocalPath))
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(row.LocalPath);
            await _api.UploadNewVersionAsync(row.Id, bytes, row.FileExtension);
            await _api.CheckInAsync(row.Id);
            EndCheckout(row.Id, row.LocalPath);
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

    // Unlock: nothing changed (or the local file is gone) — just release the lock and remove the working copy.
    [RelayCommand]
    private Task Unlock(CheckoutRowViewModel? row) => ReleaseAsync(row, discard: false);

    // Discard: abandon the local edits — release the lock without uploading, remove the working copy. The
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
            await _api.CheckInAsync(row.Id);
            EndCheckout(row.Id, row.LocalPath);
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

    // ---- Orphan resolution (ADR "Web check-out + orphaned local copy") --------------------------------

    // Add as new version: the check-out was released elsewhere, but this local copy has edits — commit them as a
    // new version of the (now unlocked) document.
    [RelayCommand]
    private async Task AddOrphanAsVersion(OrphanRowViewModel? orphan)
    {
        if (_api is null || orphan is null || !File.Exists(orphan.LocalPath))
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(orphan.LocalPath);
            await _api.UploadNewVersionAsync(orphan.Id, bytes, orphan.FileExtension);
            ResolveOrphan(orphan);
            Report($"Added '{orphan.Name}' as a new version.");
            await ReloadAllAsync();
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
        catch (Exception e)
        {
            Report($"Could not add '{orphan.Name}' as a new version: {e.Message}");
        }
    }

    // Discard an orphaned local copy — drop the file (invoked after the code-behind confirmation).
    public void DiscardOrphan(OrphanRowViewModel orphan)
    {
        ResolveOrphan(orphan);
        Report($"Discarded the orphaned copy of '{orphan.Name}'.");
        OnPropertyChanged(nameof(HasOrphans));
    }

    private void ResolveOrphan(OrphanRowViewModel orphan)
    {
        EndCheckout(orphan.Id, orphan.LocalPath);
        Orphans.Remove(orphan);
        OnPropertyChanged(nameof(HasOrphans));
    }

    // Any check-out with un-backed-up local edits (the logout/close exit guard). Recomputes local hashes first.
    public async Task<bool> HasUnsyncedEditsAsync()
    {
        await LoadAsync();
        return Items.Any(r => r.IsUnsynced);
    }

    private void EndCheckout(Guid documentId, string localPath)
    {
        TryDeleteLocal(localPath);
        RemoveSynced(documentId);
    }

    private async Task ReloadAllAsync()
    {
        await LoadAsync();
        if (OnChanged is not null)
        {
            await OnChanged();
        }
    }

    // ---- Manifest persistence -------------------------------------------------------------------------

    private void SetSynced(Guid documentId, string fileName, string sha)
    {
        _manifest[documentId] = new ManifestEntry(fileName, sha);
        SaveManifest();
    }

    private void RemoveSynced(Guid documentId)
    {
        if (_manifest.Remove(documentId))
        {
            SaveManifest();
        }
    }

    private void LoadManifest()
    {
        try
        {
            _manifest = _manifestPath is not null && File.Exists(_manifestPath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, ManifestEntry>>(File.ReadAllText(_manifestPath)) ?? []
                : [];
        }
        catch
        {
            _manifest = [];
        }
    }

    private void SaveManifest()
    {
        if (_manifestPath is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(_manifest));
        }
        catch
        {
            // Best-effort — a lost manifest just means the next reconcile re-establishes the synced baselines.
        }
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort — leaving a stray working copy is harmless.
        }
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? ComputeSha256(string path)
    {
        try
        {
            return File.Exists(path) ? Sha256Of(File.ReadAllBytes(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    // Populates the Check-out tab for the headless screenshot (no network) — one un-saved edit (offers Save to
    // cloud / Check in / Discard), one saved-to-cloud edit (Check in / Discard), one untouched (Unlock).
    internal void PopulateDemoForScreenshot()
    {
        Items.Clear();
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Contract draft", Path = "Repositories / Demo Repository / Contracts", FileExtension = ".docx", RepoSha256 = "aaaa", LocalPath = "/x/Contract draft.docx", LocalSha256 = "bbbb", SyncedSha256 = "aaaa" });
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Proposal", Path = "Repositories / Demo Repository / Sales", FileExtension = ".pptx", RepoSha256 = "dddd", LocalPath = "/x/Proposal.pptx", LocalSha256 = "eeee", SyncedSha256 = "eeee" });
        Items.Add(new CheckoutRowViewModel { Id = Guid.NewGuid(), Name = "Quarterly report", Path = "Repositories / Demo Repository / Finance", FileExtension = ".xlsx", RepoSha256 = "cccc", LocalPath = "/x/Quarterly report.xlsx", LocalSha256 = "cccc", SyncedSha256 = "cccc" });
        Orphans.Add(new OrphanRowViewModel { Id = Guid.NewGuid(), Name = "Old memo", FileExtension = ".docx", LocalPath = "/x/Old memo.docx" });
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasOrphans));
        OnPropertyChanged(nameof(Count));
        Status = "3 document(s) checked out.";
    }
}

// One row in the Check-out tab: a checked-out document + its working-copy comparison.
public sealed class CheckoutRowViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string FileExtension { get; init; }
    public required string RepoSha256 { get; init; }
    public required string LocalPath { get; init; }

    // null = no local file; otherwise the hex SHA-256 of the local working copy.
    public required string? LocalSha256 { get; init; }

    // The SHA-256 of the content that is safely backed up (cloud stash, or the checked-out original).
    public required string SyncedSha256 { get; init; }

    // When an idle check-out will be auto-released (ADR "Check-out expiry UX"); null when disabled.
    public DateTimeOffset? ExpiresAt { get; init; }
    public string ExpiresText => ExpiresAt is { } e
        ? e.LocalDateTime.ToString("g") + ((e - DateTimeOffset.UtcNow).TotalDays <= 1 ? " (soon)" : "")
        : "Never";

    public bool IsMissing => LocalSha256 is null;

    // Modified vs the repository version — there is something to commit (Check in) or discard.
    public bool IsModified => LocalSha256 is not null && !string.Equals(LocalSha256, RepoSha256, StringComparison.OrdinalIgnoreCase);

    // Un-synced: the local copy has edits not yet backed up to the cloud stash — drives Save to cloud + the guard.
    public bool IsUnsynced => LocalSha256 is not null && !string.Equals(LocalSha256, SyncedSha256, StringComparison.OrdinalIgnoreCase);

    public bool CanCheckIn => IsModified;
    public bool CanDiscard => IsModified;
    public bool CanSaveToCloud => IsUnsynced;
    public bool CanUnlock => !IsModified; // unchanged or missing — release without a new version
    public bool CanExtend => ExpiresAt is not null; // only meaningful when auto-release is enabled

    public string StatusText => IsMissing
        ? "Local file missing"
        : !IsModified ? "Unchanged"
        : IsUnsynced ? "Modified — not saved to cloud"
        : "Saved to cloud";
}

// An orphaned local working copy: the check-out was released elsewhere (web check-in / override) while this
// client held local edits. Resolved with Add as new version / Discard.
public sealed class OrphanRowViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string FileExtension { get; init; }
    public required string LocalPath { get; init; }
}
