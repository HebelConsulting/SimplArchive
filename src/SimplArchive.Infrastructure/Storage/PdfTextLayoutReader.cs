using SimplArchive.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SimplArchive.Infrastructure.Storage;

// Reads per-page word boxes from a PDF's text layer via PdfPig (Apache-2.0). Coordinates are normalized to
// 0..1 within each page with a top-left origin — PdfPig uses PDF user space (origin bottom-left, Y up), so Y
// is flipped. See ADR "Search hit overlay (text layout)". A scanned image-only PDF has no text layer, so it
// yields empty pages (no overlay) — that case is deferred to a later slice.
public static class PdfTextLayoutReader
{
    public static DocumentTextLayout Read(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);

        var pages = new List<TextLayoutPage>();
        foreach (var page in document.GetPages())
        {
            var width = page.Width;
            var height = page.Height;
            var words = new List<TextLayoutWord>();

            if (width > 0 && height > 0)
            {
                foreach (var word in page.GetWords())
                {
                    var text = word.Text;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var box = word.BoundingBox;
                    var left = Math.Min(box.Left, box.Right);
                    var right = Math.Max(box.Left, box.Right);
                    var bottom = Math.Min(box.Bottom, box.Top);
                    var top = Math.Max(box.Bottom, box.Top);

                    // Flip Y: PDF space has 0 at the bottom, the overlay wants 0 at the top.
                    words.Add(new TextLayoutWord(
                        text,
                        X: left / width,
                        Y: (height - top) / height,
                        Width: (right - left) / width,
                        Height: (top - bottom) / height));
                }
            }

            pages.Add(new TextLayoutPage(words));
        }

        return new DocumentTextLayout(pages);
    }
}
