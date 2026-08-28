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
// The hrefs are the ROW's advertised addresses (ADR 0555): a document stages through its `versions`, a folder
// recurses through its `children`; an item whose row did not carry the needed address is skipped (best-effort,
// same as a failed download).
public sealed record DragOutItem(string Name, bool IsFolder, string? VersionsHref, string? ChildrenHref,
    Guid Id = default, string DocumentType = "");

// Stages contents-list items as **real OS files in a fresh temp folder** so they can be dragged out to the OS
// filesystem (Finder/Explorer/desktop) — issue #266: a **document** → its current version file (`<stem><ext>`),
// a **folder** → a recursive **`.zip`** of its documents. Returns the staged absolute paths, which the drag
// source hands to the OS as file data. Best-effort per item (a failed download is skipped). The files must exist
// **before** `DragDrop.DoDragDrop`, so the caller awaits this first (a brief "preparing…" status is fine — an
// async download can't run *during* the drag gesture).
public static class DragOutStager
{
    public static async Task<IReadOnlyList<string>> StageAsync(
        SimplArchiveApiClient api, IReadOnlyList<DragOutItem> items, CancellationToken cancellationToken = default,
        string? combinedStem = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simplarchive-dragout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // A uniform multi-selection of contacts or calendar entries stages ONE combined file (#658): both
        // formats are record streams, and every consuming application expects many records in one file —
        // thirty separate files is thirty imports. The mask name is the signal (a Contact IS a .vcf by
        // classification); the server re-validates the stored extensions and refuses anything mixed, and a
        // refusal falls through to the per-item staging below rather than an empty drag.
        if (items.Count > 1 && items.All(i => !i.IsFolder && i.Id != default)
            && items.Select(i => i.DocumentType).Distinct().SingleOrDefault() is "Contact" or "Appointment")
        {
            try
            {
                var kind = items[0].DocumentType;
                // The key is picked OUTSIDE Strings.Get: the localisation-key scanner reads the first literal
                // inside a Get(...) as the key, and a ternary there made it hunt for a key named "Contact".
                var stemKey = kind == "Contact" ? "CombinedContactsStem" : "CombinedEventsStem";
                var stem = combinedStem is { Length: > 0 } ? combinedStem : SimplArchive.Localization.Strings.Get(stemKey);
                var (bytes, fileName) = await api.Documents.ExportCombinedAsync(
                    items.Select(i => i.Id).ToList(), stem, cancellationToken);
                var single = UniquePath(dir, Sanitize(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName));
                await File.WriteAllBytesAsync(single, bytes, cancellationToken);
                return [single];
            }
            catch (Exception)
            {
                // Fall through: one file per item is always a correct answer.
            }
        }

        var staged = new List<string>();
        foreach (var item in items)
        {
            try
            {
                staged.Add(item.IsFolder
                    ? await StageFolderZipAsync(api, item, dir, cancellationToken)
                    : await StageDocumentAsync(api,
                        item.VersionsHref ?? throw new InvalidOperationException("The row advertised no 'versions' rel (ADR 0543)."),
                        item.Name, dir, cancellationToken));
            }
            catch (Exception)
            {
                // Best-effort: skip an item that fails to download so the rest of the drag still carries files.
            }
        }

        return staged;
    }

    private static async Task<string> StageDocumentAsync(SimplArchiveApiClient api, string versionsHref, string stem, string dir, CancellationToken ct)
    {
        var preview = await api.Documents.GetPreviewAsync(versionsHref, ct);
        if (preview.DownloadUrl is null)
        {
            throw new InvalidOperationException("The document has no downloadable version.");
        }

        var bytes = await api.Versions.DownloadVersionBytesAsync(preview.DownloadUrl, ct);
        var path = UniquePath(dir, MainWindowViewModel.WithExtension(Sanitize(stem), preview.FileExtension));
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    private static async Task<string> StageFolderZipAsync(SimplArchiveApiClient api, DragOutItem folder, string dir, CancellationToken ct)
    {
        var zipPath = UniquePath(dir, Sanitize(folder.Name) + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await AddFolderAsync(api,
                folder.ChildrenHref ?? throw new InvalidOperationException("The row advertised no 'children' rel (ADR 0543)."),
                "", zip, ct);
        }

        return zipPath;
    }

    // Recursively adds a folder's documents to the zip under `prefix` (its path within the archive). A child
    // folder recurses one level deeper; a leaf document is written as an entry named `<stem><ext>`.
    private static async Task AddFolderAsync(SimplArchiveApiClient api, string childrenHref, string prefix, ZipArchive zip, CancellationToken ct)
    {
        foreach (var child in await api.Documents.GetChildrenAsync(childrenHref, ct))
        {
            var name = Sanitize(child.Name);
            if (!child.HasVersions)
            {
                await AddFolderAsync(api, child.Href("children"), prefix + name + "/", zip, ct);
                continue;
            }

            try
            {
                var preview = await api.Documents.GetPreviewAsync(child.Href("versions"), ct);
                if (preview.DownloadUrl is null)
                {
                    continue;
                }

                var bytes = await api.Versions.DownloadVersionBytesAsync(preview.DownloadUrl, ct);
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
