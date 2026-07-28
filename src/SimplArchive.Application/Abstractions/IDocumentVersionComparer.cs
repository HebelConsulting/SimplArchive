namespace SimplArchive.Application.Abstractions;

// Produces an inline unified text diff between two document versions' extracted text (ADR "Document version
// comparison"). Plain-text formats decode directly; office/PDF go through ITextExtractor (Tika) when configured.
// Available is false when either side has no extractable text (a binary/image format, or Tika unavailable).
public interface IDocumentVersionComparer
{
    Task<VersionComparison> CompareAsync(string fromObjectKey, string toObjectKey, CancellationToken cancellationToken = default);
}

public enum DiffOp
{
    Unchanged = 0,
    Added = 1,
    Removed = 2,
}

public sealed record DiffLine(DiffOp Op, string Text);

public sealed record VersionComparison(bool Available, IReadOnlyList<DiffLine> Lines);
