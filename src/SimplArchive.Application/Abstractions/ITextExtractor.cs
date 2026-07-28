namespace SimplArchive.Application.Abstractions;

// Extracts plain text from a document's bytes for full-text indexing — see ADR "Search / full-text
// indexing model" (0011). Backed by Apache Tika (a sidecar) in the OpenSearch slice; returns "" on any
// failure (unsupported format, extractor unavailable) so indexing degrades to metadata-only.
public interface ITextExtractor
{
    Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}
