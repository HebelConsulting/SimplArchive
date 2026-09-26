using SimplArchive.TextLayout;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.UnitTests;

// "PDF bytes → per-page word boxes", the one extraction the server and the desktop client now share (#1402).
//
// It became shared because the strict tier made it a CLIENT question: the text-layout endpoint refuses on such a
// tenant (409 PLAINTEXT_CONTENT_REFUSED, ADR 0829 — returning every word with its coordinates reconstructs the
// document through a route that presigns nothing), so find-in-document is computed from the bytes the card
// decrypted. Two copies of this would mean two copies of the Y-flip and the punctuation rule, and the overlay a
// strict reader sees would drift from the one everybody else sees.
public class PdfWordBoxesTests
{
    private const double PageWidth = 595;   // A4 points
    private const double PageHeight = 842;

    [Fact]
    public void A_words_box_is_normalised_with_a_TOP_left_origin()
    {
        // PDF user space puts the origin bottom-left with Y going up; every renderer here wants top-left. The
        // flip is the one piece of arithmetic in this project, so it is what a test should pin: text placed near
        // the TOP of the page must come back with a SMALL Y.
        var pdf = OnePage("Rechnungsnummer", x: 100, yFromBottom: PageHeight - 100);

        var page = Assert.Single(PdfWordBoxes.Read(pdf).Pages);
        var word = Assert.Single(page.Words);

        Assert.Equal("Rechnungsnummer", word.Text);
        Assert.InRange(word.Y, 0.0, 0.25);                       // near the top, not near the bottom
        Assert.InRange(word.X, 100.0 / PageWidth - 0.02, 100.0 / PageWidth + 0.05);
        Assert.InRange(word.Width, 0.01, 1.0);
        Assert.InRange(word.Height, 0.0, 0.2);
    }

    [Fact]
    public void Text_near_the_bottom_comes_back_near_the_bottom()
    {
        // The other end of the same flip. Without this, a sign error would pass the test above on any page whose
        // text happens to sit in the middle.
        var pdf = OnePage("Fusszeile", x: 100, yFromBottom: 60);

        var word = Assert.Single(Assert.Single(PdfWordBoxes.Read(pdf).Pages).Words);

        Assert.InRange(word.Y, 0.85, 1.0);
    }

    [Fact]
    public void Surrounding_punctuation_is_not_part_of_the_word()
    {
        // The #788 rule, applied where the word is produced: a box that copies `4711,` makes every paste a
        // paste-then-delete, and it bites hardest on exactly the tokens people copy.
        var pdf = OnePage("4711,", x: 100, yFromBottom: 400);

        Assert.Equal("4711", Assert.Single(Assert.Single(PdfWordBoxes.Read(pdf).Pages).Words).Text);
    }

    [Fact]
    public void A_page_with_no_text_layer_yields_a_page_with_no_words()
    {
        // A scanned document is this case, and it is the honest limit rather than a failure: OCR is what would
        // fill it, the desktop has no OCR engine, and the caller's job is to leave find off rather than to offer
        // a search that silently matches nothing. The PAGE still exists, so page indices stay aligned with the
        // rendered images.
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageWidth, PageHeight);

        var page = Assert.Single(PdfWordBoxes.Read(builder.Build()).Pages);

        Assert.Empty(page.Words);
    }

    [Fact]
    public void Pages_come_back_in_page_order()
    {
        // Index n must be the words of the page rendered at n — the overlay maps them positionally, so a
        // reordering here would put one page's boxes over another page's image.
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (var word in new[] { "erste", "zweite", "dritte" })
        {
            var page = builder.AddPage(PageWidth, PageHeight);
            page.AddText(word, 12, new UglyToad.PdfPig.Core.PdfPoint(100, 400), font);
        }

        var pages = PdfWordBoxes.Read(builder.Build()).Pages;

        Assert.Equal(3, pages.Count);
        Assert.Equal(["erste", "zweite", "dritte"], pages.Select(p => Assert.Single(p.Words).Text));
    }

    private static byte[] OnePage(string text, double x, double yFromBottom)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(PageWidth, PageHeight);
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(x, yFromBottom), font);
        return builder.Build();
    }
}
