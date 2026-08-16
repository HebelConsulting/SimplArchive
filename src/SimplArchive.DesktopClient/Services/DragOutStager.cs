using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Services;

// One contents-list item to stage for a drag-out (issue #266). Kept decoupled from NodeViewModel so the stager is
// unit-testable. IsFolder = a Document with no confirmed version (a folder); everything else is a leaf document.
public sealed record DragOutItem(Guid Id, string Name, bool IsFolder);

// Stages contents-list items as **real OS files in a fresh temp folder** so they can be dragged out to the OS
// filesystem (Finder/Explorer/desktop) — issue #266: a **document** → its current version file (`<stem><ext>`),
// a **folder** → a recursive **`.zip`** of its documents. Returns the staged absolute paths, which the drag
// source hands to the OS as file data. Best-effort per item (a failed download is skipped). The files must exist
// **before** `DragDrop.DoDragDrop`, so the caller awaits this first (a brief "preparing…" status is fine — an
// async download can't run *during* the drag gesture).
public static class DragOutStager
{
    public static async Task<IReadOnlyList<string>> StageAsync(
        SimplArchiveApiClient api, IReadOnlyList<DragOutItem> items, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simplarchive-dragout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var staged = new List<string>();
        foreach (var item in items)
        {
            try
            {
                staged.Add(item.IsFolder
                    ? await StageFolderZipAsync(api, item, dir, cancellationToken)
                    : await StageDocumentAsync(api, item.Id, item.Name, dir, cancellationToken));
            }
            catch (Exception)
            {
                // Best-effort: skip an item that fails to download so the rest of the drag still carries files.
            }
        }

        return staged;
    }

    private static async Task<string> StageDocumentAsync(SimplArchiveApiClient api, Guid documentId, string stem, string dir, CancellationToken ct)
    {
        var preview = await api.Documents.GetPreviewAsync(documentId, ct);
        if (preview.DownloadUrl is null)
        {
            throw new InvalidOperationException("The document has no downloadable version.");
        }

        var bytes = await api.DownloadVersionBytesAsync(preview.DownloadUrl, ct);
        var path = UniquePath(dir, MainWindowViewModel.WithExtension(Sanitize(stem), preview.FileExtension));
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    private static async Task<string> StageFolderZipAsync(SimplArchiveApiClient api, DragOutItem folder, string dir, CancellationToken ct)
    {
        var zipPath = UniquePath(dir, Sanitize(folder.Name) + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await AddFolderAsync(api, folder.Id, "", zip, ct);
        }

        return zipPath;
    }

    // Recursively adds a folder's documents to the zip under `prefix` (its path within the archive). A child
    // folder recurses one level deeper; a leaf document is written as an entry named `<stem><ext>`.
    private static async Task AddFolderAsync(SimplArchiveApiClient api, Guid folderId, string prefix, ZipArchive zip, CancellationToken ct)
    {
        foreach (var child in await api.Documents.GetChildrenAsync(folderId, ct))
        {
            var name = Sanitize(child.Name);
            if (!child.HasVersions)
            {
                await AddFolderAsync(api, child.Id, prefix + name + "/", zip, ct);
                continue;
            }

            try
            {
                var preview = await api.Documents.GetPreviewAsync(child.Id, ct);
                if (preview.DownloadUrl is null)
                {
                    continue;
                }

                var bytes = await api.DownloadVersionBytesAsync(preview.DownloadUrl, ct);
                var entry = zip.CreateEntry(prefix + MainWindowViewModel.WithExtension(name, preview.FileExtension), CompressionLevel.Fastest);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, ct);
            }
            catch (Exception)
            {
                // Skip a child that fails to download; the rest of the zip is still produced.
            }
        }
    }

    // Replaces characters not allowed in a file name (keeps the zip + temp writes portable across OSes).
    private static string Sanitize(string name)
    {
        var cleaned = string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? "item" : cleaned;
    }

    // Avoids clobbering when two selected items share a name: "report.pdf", "report (2).pdf", …
    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
