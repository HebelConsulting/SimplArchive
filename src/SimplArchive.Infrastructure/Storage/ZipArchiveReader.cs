using System.IO.Compression;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Reads .zip archives on demand (ADR "Zip file browsing") via System.IO.Compression — no extra dependency,
// nothing persisted. Caps entry count and per-entry size to guard against decompression bombs.
public class ZipArchiveReader : IArchiveReader
{
    private const int MaxEntries = 2000;
    private const long MaxEntrySize = 100L * 1024 * 1024; // 100 MB uncompressed per entry

    public IReadOnlyList<ArchiveEntry> ListEntries(Stream seekableArchive)
    {
        using var archive = new ZipArchive(seekableArchive, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new List<ArchiveEntry>();

        foreach (var entry in archive.Entries)
        {
            // A directory entry has an empty Name (its FullName ends in '/'); skip it — we list files only.
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            entries.Add(new ArchiveEntry(entry.Name, entry.FullName, entry.Length));

            if (entries.Count >= MaxEntries)
            {
                break;
            }
        }

        return entries;
    }

    public byte[]? ReadEntry(Stream seekableArchive, string entryPath)
    {
        using var archive = new ZipArchive(seekableArchive, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        if (entry.Length > MaxEntrySize)
        {
            throw new InvalidOperationException($"Archive entry '{entryPath}' is too large to read ({entry.Length} bytes).");
        }

        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async Task ReadEntriesAsync(Stream seekableArchive, Func<ArchiveEntry, Stream, Task> onEntryAsync, CancellationToken cancellationToken = default)
    {
        using var archive = new ZipArchive(seekableArchive, ZipArchiveMode.Read, leaveOpen: true);
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) || entry.Length > MaxEntrySize)
            {
                continue;
            }

            using var entryStream = entry.Open();
            await onEntryAsync(new ArchiveEntry(entry.Name, entry.FullName, entry.Length), entryStream);

            if (++count >= MaxEntries)
            {
                break;
            }
        }
    }
}
