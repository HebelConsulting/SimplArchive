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
    public static bool IsConvertibleScan(byte[] pdfBytes)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
        {
            return false;
        }

        try
        {
            // A wrong/absent password throws PdfDocumentEncryptedException; a corrupt file throws too — either
            // way we can't safely convert, so treat as "not a scan".
            using var document = PdfDocument.Open(pdfBytes);

            if (IsSigned(document))
            {
                return false;
            }

            var hasImage = false;
            foreach (var page in document.GetPages())
            {
                // Any extractable text anywhere ⇒ not an image-only scan (born-digital or already searchable).
                if (page.GetWords().Any(w => !string.IsNullOrWhiteSpace(w.Text)))
                {
                    return false;
                }

                if (!hasImage && page.GetImages().Any())
                {
                    hasImage = true;
                }
            }

            return hasImage;
        }
        catch (Exception)
        {
            return false;
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
