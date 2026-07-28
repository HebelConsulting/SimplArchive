using System.Text;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Search;

// Builds the searchable text for a .zip document (ADR "Zip file browsing"): the first-level entries' paths
// plus each entry's Tika-extracted text — one archive deep (a nested archive is indexed by its name but not
// opened), so a search finds the zip that contains a file. Shared by the per-doc indexer and the full
// rebuilder. The zip is never unpacked into the DMS; this only feeds OpenSearch.
public static class ArchiveContentExtractor
{
    private const long MaxZipBytes = 200L * 1024 * 1024;

    private static readonly HashSet<string> NestedArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz" };

    public static bool IsZip(string objectKey) =>
        Path.GetExtension(objectKey).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public static async Task<string> ExtractAsync(
        IObjectStorageClient storage, IArchiveReader archiveReader, ITextExtractor extractor,
        string objectKey, CancellationToken cancellationToken)
    {
        await using var raw = await storage.GetObjectAsync(objectKey, cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await raw.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxZipBytes)
            {
                return ""; // too large to index; it stays browsable via the archive endpoint
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;

        var builder = new StringBuilder();
        try
        {
            await archiveReader.ReadEntriesAsync(buffer, async (entry, stream) =>
            {
                builder.Append(entry.Path).Append('\n'); // the path/filename is always indexed
                if (NestedArchiveExtensions.Contains(Path.GetExtension(entry.Path)))
                {
                    return; // first level only — don't open a nested archive's contents
                }

                var text = await extractor.ExtractAsync(stream, "application/octet-stream", cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Append(text).Append('\n');
                }
            }, cancellationToken);
        }
        catch (Exception)
        {
            // Corrupt/unsupported archive — index whatever was gathered rather than fail (and retry forever).
        }

        return builder.ToString();
    }
}
