using SimplArchive.Infrastructure.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.Api.Inbox;

/// <summary>
/// A sample batch scan (issue #492): real documents separated by two <see cref="PatchCodePage"/> sheets, in one
/// file — what a stack of paper looks like after it has been through a feeder.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stack:</b> an invoice, a separator, a two-page maintenance agreement whose SECOND page came through
/// upside-down and whose duplex back came through blank, a separator, and a second invoice. Seven sheets,
/// three documents — and every defect in it is one the product can actually correct: auto-rotate turns the
/// second page round, and the blank duplex back is dropped with Sort pages, which is precisely the case that
/// produces such a page in real life.
/// </para>
/// <para>
/// The documents are the <b>demo seed's own PDFs</b>, not generated pages. The first version of this fixture
/// drew its own text and filled the pages with "The quick brown fox jumps over the lazy dog" — lorem in all but
/// name, on the first artefact anybody feeds through the feature. Reusing the seed documents costs nothing, and
/// the batch now reads like a real day's post: an invoice, an offer with its revision stapled behind it, and a
/// second invoice.
/// </para>
/// <para>
/// <b>One page arrives upside-down; none is crooked.</b> That split is the point, and it mirrors what the
/// product can actually do to a PDF: rotation is only the page's <c>/Rotate</c> attribute, so it is lossless
/// and auto-rotate corrects it — while deskew cannot happen without re-rendering, so it declines PDFs and
/// would leave a crooked page crooked. A sample must not demonstrate a feature that cannot act on it.
/// </para>
/// <para>
/// The upside-down page is rotated by TRANSFORMING ITS CONTENT, not by setting <c>/Rotate</c>: a scanner that
/// feeds a sheet in backwards produces upside-down pixels, and the correction being demonstrated is precisely
/// the one that detects that and writes the <c>/Rotate</c> which was missing. Setting it here would hand the
/// pipeline the answer.
/// </para>
/// <para>
/// It lives in the Api rather than beside <see cref="PatchCodePage"/> in Infrastructure because that is where
/// the seed documents are embedded; the separator sheet itself is still the real printable one, page for page,
/// so the sample cannot drift from what the detector is taught to find.
/// </para>
/// </remarks>
public static class PatchCodeSampleBatch
{
    /// <summary>Which pages are separator sheets, 1-based — what a detector must find.</summary>
    public static IReadOnlyList<int> SeparatorPages { get; } = [2, 6];

    /// <summary>How many pages the batch has, so a test can state the arithmetic rather than discover it.</summary>
    public const int PageCount = 7;

    /// <summary>The blank back of a duplex-scanned sheet — the page a user is meant to delete (1-based).</summary>
    public const int BlankPage = 5;

    // An offer and its revision count as ONE document: they arrive stapled together, which is exactly why the
    // batch needs a multi-page document in it. Without one, "cut at the separators" and "split every page"
    // produce the same answer, and a fixture that cannot tell those apart is not testing the feature.
    private static readonly Sheet[] Sheets =
    [
        new("DemoInvoice.pdf", 1),
        Separator,
        new("DemoMaintenanceAgreement.pdf", 1),
        new("DemoMaintenanceAgreement.pdf", 2, UpsideDown: true),
        Blank,
        Separator,
        new("DemoChocInvoiceV1.pdf", 1),
    ];

    /// <summary>One sheet of the stack: a page of a demo document, a separator, or a blank duplex back.</summary>
    private sealed record Sheet(string? Source, int Page, bool UpsideDown = false, bool IsBlank = false);

    private static Sheet Separator => new(null, 0);

    private static Sheet Blank => new(null, 0, IsBlank: true);

    /// <summary>Which page arrives upside-down, 1-based — the one auto-rotate is there to correct.</summary>
    public const int UpsideDownPage = 4;

    /// <summary>The batch, as one PDF: document, separator, document (2pp), separator, document.</summary>
    public static byte[] CreatePdf()
    {
        var builder = new PdfDocumentBuilder();

        foreach (var sheet in Sheets)
        {
            if (sheet.IsBlank)
            {
                // A4, and nothing on it. Duplex scanning a single-sided sheet produces exactly this, and it is
                // the page the user is meant to notice and drop with Sort pages — the feature has no better
                // demonstration than the case that produces it in real life.
                builder.AddPage(A4Width, A4Height);
                continue;
            }

            var bytes = sheet.Source is null ? PatchCodePage.CreatePdf() : Embedded(sheet.Source);
            using var document = PdfDocument.Open(bytes);
            var added = builder.AddPage(document, sheet.Source is null ? 1 : sheet.Page);

            if (sheet.UpsideDown)
            {
                TurnUpsideDown(added);
            }
        }

        return builder.Build();
    }

    // /Rotate 180, which is how a PDF says "display this page upside-down" — the page then renders exactly
    // as a sheet fed in backwards would, which is what the correction has to detect.
    //
    // It does NOT hand the pipeline the answer: auto-rotate rasterises the page AS DISPLAYED, sees upside-down
    // text, and writes the rotation that puts it right. The answer would be a page that already displays
    // correctly. Transforming the content instead was tried and does not work here — a page copied from
    // another document keeps its own content stream, so a `cm` prepended to the builder's operation list
    // applies to nothing, and the sample comes out entirely upright while still looking correct.
    private static void TurnUpsideDown(PdfPageBuilder page) => page.SetRotation(new PageRotationDegrees(180));

    private const double A4Width = 595;
    private const double A4Height = 842;

    /// <summary>The same batch as a scan: a bilevel multi-page TIFF, checked in beside the demo documents.</summary>
    /// <remarks>
    /// Not composed here like the PDF, because composing it means rasterising PDFs and the Api image has no
    /// rasteriser — that is what the OCR sidecar is for, and asking it at request time would make the sample
    /// unavailable exactly where OCR is not configured. <c>scripts/generate-scan-sample.sh</c> rebuilds it.
    /// </remarks>
    public static byte[] CreateTiff() => Embedded("DemoPatchCodeSampleBatch.tif");

    private static byte[] Embedded(string logicalName)
    {
        using var stream = typeof(PatchCodeSampleBatch).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"The demo document '{logicalName}' is not embedded in the Api assembly, so the patch-code "
                + "sample batch cannot be built. See SimplArchive.Api.csproj's DemoData EmbeddedResource items.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
