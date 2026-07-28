namespace SimplArchive.Application.Abstractions;

// One file entry inside an archive (ADR "Zip file browsing"). Path is the full in-archive path (e.g.
// "docs/readme.txt"); Name is its last segment; Size is the uncompressed byte length. Directory entries
// aren't returned.
public sealed record ArchiveEntry(string Name, string Path, long Size);

// Reads a .zip on demand without extracting it into the DMS (ADR "Zip file browsing"). The stream must be
// seekable (ZipArchive reads the central directory at the end) — the caller buffers the object first.
public interface IArchiveReader
{
    // Lists the archive's file entries (flat, full paths). Capped to guard against pathological archives.
    IReadOnlyList<ArchiveEntry> ListEntries(Stream seekableArchive);

    // Reads a single entry's bytes, or null if the path isn't in the archive. Throws if the entry exceeds
    // the size cap (a decompression-bomb guard).
    byte[]? ReadEntry(Stream seekableArchive, string entryPath);

    // Single-pass visit of every file entry with its open (forward-only) stream — used for indexing an
    // archive's contents (ADR "Zip file browsing"). Skips oversized entries; caps the count.
    Task ReadEntriesAsync(Stream seekableArchive, Func<ArchiveEntry, Stream, Task> onEntryAsync, CancellationToken cancellationToken = default);
}
