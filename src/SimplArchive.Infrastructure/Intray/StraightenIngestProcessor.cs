using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;
using SimplArchive.Infrastructure.Conversion;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// Straightens crooked scans arriving in the intray (issue #491) — the first step of the ingest pipeline, and
/// deliberately the first: everything downstream reads the page more reliably when it is straight.
/// </summary>
/// <remarks>
/// <para>
/// The work is the OCR sidecar's, which already ships OCRmyPDF's <c>--deskew</c> (Leptonica's sub-degree
/// correction) and <c>--rotate-pages</c> (Tesseract's orientation detection, for a page that is 90 or 180
/// degrees out). It is nearly free on this path, because a TIFF was going to be rasterised on its way to a
/// searchable PDF anyway — and it improves the OCR in the same pass, since Tesseract reads straight text
/// better.
/// </para>
/// <para>
/// <b>It declines far more often than it acts</b>, and each refusal is a deliberate one:
/// </para>
/// <list type="bullet">
/// <item>The user turned it off (<c>User.DeskewIntrayUploads</c>).</item>
/// <item>Neither correction was asked for. They are TWO settings, because they cost differently.</item>
/// <item>DESKEW on anything but a TIFF. Sub-degree correction cannot be applied without re-rendering the page,
/// and doing that to a digital-born PDF trades real text for an OCR approximation — a strictly worse
/// document.</item>
/// <item>The TIFF does not look like a scan (<see cref="TiffTraits"/>). OCRmyPDF only emits PDF, so processing
/// changes the format whether or not anything was corrected; for a photograph that is a conversion which gains
/// nothing.</item>
/// </list>
/// <para>
/// A sidecar failure returns null too, so the item is left exactly as it arrived. Straightening is a
/// convenience, and losing it must never cost the user their file.
/// </para>
/// </remarks>
public sealed class StraightenIngestProcessor(
    SimplArchiveDbContext dbContext,
    ISearchablePdfConverter converter) : IIntrayIngestProcessor
{
    public string Name => "straighten";

    public async Task<IntrayProcessed?> TryProcessAsync(IntrayIngestContext context, CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf(context.Name);
        var isTiff = format == PageComposer.PageFormat.Tiff;
        var kind = format switch
        {
            PageComposer.PageFormat.Tiff => SearchablePdfSourceKind.Tiff,
            PageComposer.PageFormat.Pdf => SearchablePdfSourceKind.Pdf,
            _ => (SearchablePdfSourceKind?)null,
        };

        if (kind is not { } sourceKind)
        {
            return null;
        }

        // The preferences are the USER's, and the sweep reads them for items that arrived over WebDAV where no
        // client was involved — which is why they are columns and not client-side settings.
        var wanted = await dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == context.UserId)
            .Select(u => new { u.RotateIntrayUploads, u.DeskewIntrayUploads })
            .FirstOrDefaultAsync(cancellationToken);

        // Rotation may run on either format. Deskew needs a re-render, so it runs on a TIFF — and, since the
        // review of 2026-08-16, on a PDF the detector classifies as a SCAN: its text layer is OCR output
        // already, so the re-render trades nothing real away (a digital-born PDF keeps the lossless-only rule).
        var rotate = wanted?.RotateIntrayUploads == true;
        var deskew = wanted?.DeskewIntrayUploads == true
            && (isTiff || (format == PageComposer.PageFormat.Pdf && ScannedPdfDetector.IsConvertibleScan(context.Bytes)));

        if (!rotate && !deskew)
        {
            return null;
        }

        // A photograph is not a scan, so straightening it is a conversion that gains nothing. The trait test is
        // about TIFFs; a PDF that reached the intray is a document by construction.
        if (isTiff && !TiffTraits.LooksLikeAScannedDocument(context.Bytes, PageComposer.CountPages(context.Bytes, PageComposer.PageFormat.Tiff)))
        {
            return null;
        }

        // ONE call, however many corrections were asked for. Two processors would be worse than wasteful: the
        // first would turn a TIFF into a PDF, and the second would then decline it for being a PDF — so a TIFF
        // would silently lose its deskew the moment rotation was also enabled.
        var straightened = await converter.ConvertToSearchablePdfAsync(
            context.Bytes,
            sourceKind,
            OcrLanguagesFor(),
            deskew: deskew,
            rotate: rotate,
            cancellationToken: cancellationToken);

        return straightened is null ? null : new IntrayProcessed(straightened, ".pdf", "application/pdf");
    }

    // The sidecar's own default set. Per-version OCR languages (ADR 0272) are a property of a filed document,
    // and nothing in the intray has been filed yet — so there is nothing more specific to ask here.
    private static string OcrLanguagesFor() => "eng+deu+fra+ita";
}
