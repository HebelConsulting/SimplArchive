using UglyToad.PdfPig;

namespace SimplArchive.TextLayout;

/// <summary>One word and its box, normalised to 0..1 within its page with a TOP-LEFT origin.</summary>
/// <remarks>
/// Normalised because the caller renders the page at whatever size it likes and scales the box to match; and
/// top-left because that is what every renderer here wants. PDF user space has its origin bottom-left with Y
/// going up, so <see cref="PdfWordBoxes"/> flips it — the one piece of arithmetic in this project, and the
/// reason it is shared rather than written per surface.
/// </remarks>
public sealed record WordBox(string Text, double X, double Y, double Width, double Height);

/// <summary>One page's words, in no particular order; an empty list means the page carries no text layer.</summary>
public sealed record PageWords(IReadOnlyList<WordBox> Words);

/// <summary>A document's pages, in page order, so index <c>n</c> is the words of the page rendered at <c>n</c>.</summary>
public sealed record WordLayout(IReadOnlyList<PageWords> Pages);

/// <summary>
/// Reads per-page word boxes from a PDF's text layer.
/// </summary>
/// <remarks>
/// <para>
/// The server has done this since the search hit-overlay (ADR "Search hit overlay (text layout)") and it was a
/// private detail of the server until the strict tier made it a client question: on a strict tenant the
/// text-layout endpoint answers <c>409 PLAINTEXT_CONTENT_REFUSED</c>, because every word with its coordinates
/// reconstructs the document through a route that presigns nothing (ADR 0829). Find-in-document therefore has
/// to be computed where the document is already in the clear — the desktop client, after the card opens the
/// envelope (#1402, ADR 0830).
/// </para>
/// <para>
/// <b>A scanned page yields nothing, and that is the honest answer rather than a failure.</b> An image-only PDF
/// has no text layer, so its page comes back with an empty word list; OCR is what would fill it, and the client
/// has no OCR engine and should not grow one. The caller's job is to say "this document carries no text" rather
/// than to leave a find box that silently matches nothing.
/// </para>
/// </remarks>
public static class PdfWordBoxes
{
    public static WordLayout Read(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);

        var pages = new List<PageWords>();
        foreach (var page in document.GetPages())
        {
            var width = page.Width;
            var height = page.Height;
            var words = new List<WordBox>();

            if (width > 0 && height > 0)
            {
                foreach (var word in page.GetWords())
                {
                    // Trimmed BEFORE the empty check, so a token that is only punctuation drops out entirely
                    // rather than becoming a clickable box that copies nothing (#788).
                    var text = WordValue.Trim(word.Text);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var box = word.BoundingBox;
                    var left = Math.Min(box.Left, box.Right);
                    var right = Math.Max(box.Left, box.Right);
                    var bottom = Math.Min(box.Bottom, box.Top);
                    var top = Math.Max(box.Bottom, box.Top);

                    // Flip Y: PDF space has 0 at the bottom, every renderer here wants 0 at the top.
                    words.Add(new WordBox(
                        text,
                        X: left / width,
                        Y: (height - top) / height,
                        Width: (right - left) / width,
                        Height: (top - bottom) / height));
                }
            }

            pages.Add(new PageWords(words));
        }

        return new WordLayout(pages);
    }
}
