using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;

namespace SimplArchive.Infrastructure.Conversion;

// Decides whether a PDF is a scanned image-only document that should be OCR'd into a searchable-PDF successor
// (ADR "Scanned image-only PDF detection"), via the already-present PdfPig (Apache-2.0). A PDF qualifies iff
// EVERY page has no extractable words (so born-digital / already-OCR'd PDFs are excluded — this is also the
// loop guard, since a produced successor has a text layer) AND at least one page carries a bitmap image (so a
// text-free vector PDF, with nothing to OCR, is excluded). Digitally-signed PDFs (OCR would break the
// signature), encrypted PDFs (can't inspect), and anything PdfPig can't parse are conservatively left alone.
public static class ScannedPdfDetector
{
    /// <summary>Why a PDF is, or is not, a convertible scan — so a caller can tell the two NOs apart.</summary>
    /// <remarks>
    /// The distinction exists because it could not be made before: every failure returned plain <c>false</c>,
    /// identical to a confident "this is born-digital". So a corrupt or encrypted PDF was silently never
    /// OCR'd, the document never became searchable, and nothing anywhere said why — the user's search simply
    /// did not find it (#595, ADR 0626). Leaving such a file alone is still the right behaviour; being quiet
    /// about it was not.
    /// </remarks>
    public enum ScanVerdict
    {
        /// <summary>An image-only scan with nothing extractable — convert it.</summary>
        ConvertibleScan,

        /// <summary>Read successfully, and deliberately not a candidate (has text, is signed, has no image).</summary>
        NotAScan,

        /// <summary>Could not be read at all — encrypted, corrupt, or beyond the parser. Left alone, and worth saying.</summary>
        Unreadable,
    }

    public static bool IsConvertibleScan(byte[] pdfBytes) => Detect(pdfBytes) == ScanVerdict.ConvertibleScan;

    /// <summary>The verdict, including whether the file could be read at all.</summary>
    public static ScanVerdict Detect(byte[] pdfBytes)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
        {
            return ScanVerdict.NotAScan;
        }

        try
        {
            // A wrong/absent password throws PdfDocumentEncryptedException; a corrupt file throws too — either
            // way we can't safely convert, so treat as "not a scan".
            using var document = PdfDocument.Open(pdfBytes);

            if (IsSigned(document))
            {
                return ScanVerdict.NotAScan;
            }

            var hasImage = false;
            foreach (var page in document.GetPages())
            {
                // Any extractable text anywhere ⇒ not an image-only scan (born-digital or already searchable).
                if (page.GetWords().Any(w => !string.IsNullOrWhiteSpace(w.Text)))
                {
                    return ScanVerdict.NotAScan;
                }

                if (!hasImage && page.GetImages().Any())
                {
                    hasImage = true;
                }
            }

            return hasImage ? ScanVerdict.ConvertibleScan : ScanVerdict.NotAScan;
        }
        catch (Exception)
        {
            // Encrypted, corrupt, or beyond PdfPig. Still left alone — but now the caller can SAY so instead
            // of reporting it as an ordinary "not a scan".
            return ScanVerdict.Unreadable;
        }
    }

    private static bool IsSigned(PdfDocument document)
    {
        try
        {
            // Per the PDF spec, a document containing signatures sets the AcroForm's SigFlags "SignaturesExist"
            // bit — the canonical signal. OCR would rasterize the pages and invalidate any signature.
            return document.TryGetForm(out var form) && form is not null
                && form.SignatureFlags.HasFlag(SignatureFlags.SignaturesExist);
        }
        catch (Exception)
        {
            // If the form can't be read, don't claim it's signed; the caller's other guards still apply.
            return false;
        }
    }
}
