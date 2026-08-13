namespace SimplArchive.Application.Abstractions;

// Produces the inline preview URL for a stored document version. For browser-viewable formats this is just
// the object's own inline presigned URL; for formats the browser can't render (e.g. TIFF) it generates —
// on demand and caches — a browser-viewable PNG rendition and returns a presigned URL to that instead. See
// ADR "Server-side preview renditions for non-browser-viewable images".
public interface IDocumentPreviewService
{
    // objectKey: the stored version's object key. fileName: cosmetic Content-Disposition filename (inline,
    // so not used for saving). Returns the preview URL plus whether it's a converted rendition (rather than
    // the original file shown as-is), or null when no browser-viewable preview can be produced (a
    // rendition-requiring format whose conversion failed — e.g. the Gotenberg converter is down). The caller
    // omits the preview link in that case so the client shows "No preview available" rather than a blank
    // pane; the failure isn't cached, so a later request retries. See ADR "Preview fallback when a rendition
    // can't be produced".
    // sourceMayHaveChanged: the bytes at objectKey can be REWRITTEN under the same key (the check-out
    // working-copy stash is, on every save over WebDAV). The rendition cache is keyed on the source path, so
    // reusing it there would serve the previous edit's rendition — a wrong document, shown confidently. Such a
    // caller passes true and the rendition is regenerated. Leave it false for an immutable source (a confirmed
    // document version), which is the overwhelming majority and must stay cached.
    Task<DocumentPreview?> GetPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default, bool sourceMayHaveChanged = false);

    // The object key whose bytes the client actually displays for this document — the cached preview rendition
    // when the format needs one (generated on demand, like GetPreviewUrlAsync), else the original key. Used by
    // the text-layout service so hit-overlay coordinates align with exactly what's shown. Throws if a required
    // rendition can't be produced (the caller treats that as "no overlay"). See ADR "Search hit overlay".
    Task<string> GetDisplayObjectKeyAsync(string objectKey, CancellationToken cancellationToken = default);

    // Ordered preview page URLs when the document renders to *multiple images* — currently only a multi-page
    // TIFF, whose pages (possibly of differing sizes) each become their own PNG rendition. Returns null for
    // every other format (single image, PDF, office, text): those keep the single GetPreviewUrlAsync flow (a
    // PDF is one object the client splits into pages itself). See ADR "Multi-page TIFF preview pages".
    Task<PreviewPages?> GetPreviewPagesAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default);

    // The ordered object keys the client displays, one per preview page — N for a multi-page TIFF (its per-page
    // PNGs), else the single GetDisplayObjectKeyAsync result. Lets the text-layout service produce a per-page
    // overlay aligned with the per-page preview. See ADR "Multi-page TIFF preview pages".
    Task<IReadOnlyList<string>> GetDisplayObjectKeysAsync(string objectKey, CancellationToken cancellationToken = default);
}

// An ordered set of preview page URLs (each a presigned inline URL to one page's image rendition) plus whether
// they're server-generated renditions. Only produced for a multi-page TIFF today.
public sealed record PreviewPages(IReadOnlyList<Uri> Urls, bool IsConverted);

// Url: a short-lived presigned GET URL the browser can render in place. IsConverted: true when Url points at
// a server-generated rendition (TIFF->PNG, office/email/html/markdown->PDF, JSON/XML pretty-printed) rather
// than the original file shown as-is (PDF, image, .txt) — the client badges a converted preview to show it
// isn't the original. See ADR "Converted-preview overlay badge".
public record DocumentPreview(Uri Url, bool IsConverted);
