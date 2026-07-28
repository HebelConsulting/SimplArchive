using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Comparison;

// Inline unified text diff between two versions (ADR "Document version comparison"). Reads each blob, turns it
// into plain text (text formats decode directly; everything else goes through Tika), and runs DiffPlex's inline
// diff. Available is false when either side yields no text (binary/image, or Tika not configured).
public sealed class DocumentVersionComparer : IDocumentVersionComparer
{
    // Formats decoded straight to UTF-8 text — no Tika needed (so comparison works on a Tika-less deployment).
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".log", ".html", ".htm", ".yml", ".yaml", ".rtf", ".tsv",
    };

    private readonly IObjectStorageClient _objectStorage;
    private readonly ITextExtractor _textExtractor;

    public DocumentVersionComparer(IObjectStorageClient objectStorage, ITextExtractor textExtractor)
    {
        _objectStorage = objectStorage;
        _textExtractor = textExtractor;
    }

    public async Task<VersionComparison> CompareAsync(string fromObjectKey, string toObjectKey, CancellationToken cancellationToken = default)
    {
        var fromText = await ExtractTextAsync(fromObjectKey, cancellationToken);
        var toText = await ExtractTextAsync(toObjectKey, cancellationToken);

        if (fromText is null || toText is null)
        {
            return new VersionComparison(false, []);
        }

        var diff = InlineDiffBuilder.Diff(fromText, toText);
        var lines = diff.Lines.Select(l => new DiffLine(l.Type switch
        {
            ChangeType.Inserted => DiffOp.Added,
            ChangeType.Deleted => DiffOp.Removed,
            _ => DiffOp.Unchanged,
        }, l.Text)).ToList();

        return new VersionComparison(true, lines);
    }

    // The version's text, or null when it can't be extracted (binary/image, or Tika unavailable → "").
    private async Task<string?> ExtractTextAsync(string objectKey, CancellationToken cancellationToken)
    {
        await using var stream = await _objectStorage.GetObjectAsync(objectKey, cancellationToken);

        // A known text format: decode the bytes directly (reliable without Tika).
        if (TextExtensions.Contains(Path.GetExtension(objectKey)))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        // Otherwise route through the text extractor (Tika) — "" means unsupported / not configured.
        var text = await _textExtractor.ExtractAsync(stream, "application/octet-stream", cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
