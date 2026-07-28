namespace SimplArchive.Application.Abstractions;

// A single recognized word and its box, normalized to 0..1 within its page (top-left origin), so a client can
// scale the box to whatever size it renders the page at. See ADR "Search hit overlay (text layout)".
public sealed record TextLayoutWord(string Text, double X, double Y, double Width, double Height);

// One page's words. A scanned image is a single page; a PDF has one entry per page, in page order — matching
// how the client renders the pages so an index maps a page's words to that page's rendered image.
public sealed record TextLayoutPage(IReadOnlyList<TextLayoutWord> Words);

public sealed record DocumentTextLayout(IReadOnlyList<TextLayoutPage> Pages);

// Produces per-page word boxes for the object a client actually displays for a document version — the cached
// preview rendition when the format needs one (image->PNG, office/email/…->PDF), else the original — so
// hit-overlay boxes align with exactly what's shown. Images go through OCR (Tesseract hOCR via Tika); PDFs
// through their text layer (PdfPig). null when the format has no overlay support (e.g. plain text) or
// extraction isn't available. The result is cached as a sidecar object. See ADR "Search hit overlay".
public interface IDocumentTextLayoutService
{
    Task<DocumentTextLayout?> GetTextLayoutAsync(string objectKey, CancellationToken cancellationToken = default);
}

// Extracts a single-page (or multi-page) word layout from a raster image via OCR (Tesseract hOCR through
// Tika), normalized 0..1; null when OCR isn't configured or fails. Split out so the text-layout service stays
// storage/format orchestration and the Tika call lives behind its own seam.
public interface IImageTextLayoutExtractor
{
    Task<DocumentTextLayout?> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}
