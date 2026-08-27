namespace SimplArchive.Application.Abstractions;

// A single recognized word and its box, normalized to 0..1 within its page (top-left origin), so a client can
// scale the box to whatever size it renders the page at. See ADR "Search hit overlay (text layout)".
public sealed record TextLayoutWord(string Text, double X, double Y, double Width, double Height);

/// <summary>Which characters are part of a word's VALUE, and which belong to the sentence around it (#788).</summary>
/// <remarks>
/// <para>
/// The overlay exists so a value can be lifted out of a scanned document and pasted where a value is expected —
/// a search box, an index field, a mail. Punctuation is an artefact of the sentence the word happened to sit
/// in, so <c>Rechnungsnummer:</c> and <c>4711,</c> made every paste a paste-then-delete, undoing the whole
/// saving. It bites hardest on exactly the tokens people copy: invoice numbers, dates and reference codes are
/// the ones that terminate a line or precede a colon.
/// </para>
/// <para>
/// Applied HERE, where the word is produced, rather than at each clipboard. Both clients draw from these boxes
/// and both would otherwise need their own copy of the rule — one of them in JavaScript, where a shared C#
/// helper cannot reach without an interop round trip on every click. Trimming at the source also keeps the
/// overlay's tooltip and the copied value in agreement for free, and makes FIND match the value rather than
/// the punctuation that followed it.
/// </para>
/// <para>
/// Trimmed at BOTH ends and uniformly: a leading <c>(</c> or <c>„</c> has the same problem, and a rule that
/// depended on the token's shape — keeping a trailing comma after digits for German decimals, say — would make
/// two visually similar words copy differently with nothing on screen to explain why. A number truncated at a
/// comma is a broken token either way. Predictable beats clever.
/// </para>
/// </remarks>
public static class TextLayoutValue
{
    // The characters that end or open a phrase.
    //
    // NOT the hyphen-minus, '/' or '_', which appear INSIDE reference codes and dates — trimming only at the
    // ends is what keeps `AB-1234/X` and `2026-08-27` whole. The EN and EM dashes are here though: they are
    // punctuation a typesetter added, never part of a code, so a lone one is a box that would copy nothing
    // useful. And not '%' or a currency sign, which are part of what was written.
    private static readonly char[] Punctuation = ['.', ',', ':', ';', '!', '?', '…', '(', ')', '[', ']', '{', '}',
        '"', '\'', '„', '“', '”', '«', '»', '‚', '‘', '’', '–', '—'];

    /// <summary>The word with surrounding punctuation removed; empty when nothing else remains.</summary>
    public static string Trim(string raw) => raw.Trim().Trim(Punctuation);
}

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
