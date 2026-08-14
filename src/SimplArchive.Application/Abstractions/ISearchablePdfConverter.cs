namespace SimplArchive.Application.Abstractions;

// The source format handed to the OCR sidecar. A TIFF is a pure page image (rasterize + OCR); a scanned PDF
// already carries page images (OCR them, but preserve the images). See ADR "Scanned image-only PDF detection".
public enum SearchablePdfSourceKind
{
    Tiff,
    Pdf,
}

// Converts a (multi-page) TIFF or a scanned image-only PDF into a searchable PDF — the page image plus an
// invisible, positioned OCR text layer, so the result is selectable/searchable. Backs the auto
// "TIFF/scan → searchable-PDF successor version" workflow (ADRs "Searchable PDF successor for TIFFs" and
// "Scanned image-only PDF detection"). Returns null on failure or when no converter is configured (the caller
// then leaves the source version as-is).
public interface ISearchablePdfConverter
{
    // languages: a Tesseract language string (e.g. "eng+deu+fra+ita") applied to this conversion, passed per
    // request so it can vary per call. kind selects the sidecar's OCR mode for the source format.
    //
    // deskew (#491) additionally straightens the pages: Leptonica's sub-degree correction plus Tesseract's
    // orientation detection. A parameter on this call rather than a second client, because it is one more flag
    // on a request this already makes — and nearly free, since the pages are being rasterised either way.
    Task<byte[]?> ConvertToSearchablePdfAsync(byte[] sourceBytes, SearchablePdfSourceKind kind, string languages, bool deskew = false, CancellationToken cancellationToken = default);
}
