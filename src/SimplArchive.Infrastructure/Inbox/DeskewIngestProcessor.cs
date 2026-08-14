using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Infrastructure.Inbox;

/// <summary>
/// Straightens crooked scans arriving in the inbox (issue #491) — the first step of the ingest pipeline, and
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
/// <item>The user turned it off (<c>User.DeskewInboxUploads</c>).</item>
/// <item>The file is not a TIFF. A digital-born PDF has nothing to straighten, and re-rasterising it would
/// lose its real text to an OCR approximation — a strictly worse document.</item>
/// <item>The TIFF does not look like a scan (<see cref="TiffTraits"/>). OCRmyPDF only emits PDF, so processing
/// changes the format whether or not anything was corrected; for a photograph that is a conversion which gains
/// nothing.</item>
/// </list>
/// <para>
/// A sidecar failure returns null too, so the item is left exactly as it arrived. Straightening is a
/// convenience, and losing it must never cost the user their file.
/// </para>
/// </remarks>
public sealed class DeskewIngestProcessor(
    SimplArchiveDbContext dbContext,
    ISearchablePdfConverter converter) : IInboxIngestProcessor
{
    public string Name => "deskew";

    public async Task<InboxProcessed?> TryProcessAsync(InboxIngestContext context, CancellationToken cancellationToken)
    {
        if (PageComposer.FormatOf(context.Name) != PageComposer.PageFormat.Tiff)
        {
            return null;
        }

        // The preference is the USER's, and the sweep reads it for items that arrived over WebDAV where no
        // client was involved — which is why it is a column and not a client-side setting.
        var wanted = await dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == context.UserId)
            .Select(u => (bool?)u.DeskewInboxUploads)
            .FirstOrDefaultAsync(cancellationToken);

        if (wanted != true)
        {
            return null;
        }

        var pageCount = PageComposer.CountPages(context.Bytes, PageComposer.PageFormat.Tiff);
        if (!TiffTraits.LooksLikeAScannedDocument(context.Bytes, pageCount))
        {
            return null;
        }

        var straightened = await converter.ConvertToSearchablePdfAsync(
            context.Bytes,
            SearchablePdfSourceKind.Tiff,
            OcrLanguagesFor(),
            deskew: true,
            cancellationToken);

        return straightened is null ? null : new InboxProcessed(straightened, ".pdf", "application/pdf");
    }

    // The sidecar's own default set. Per-version OCR languages (ADR 0272) are a property of a filed document,
    // and nothing in the inbox has been filed yet — so there is nothing more specific to ask here.
    private static string OcrLanguagesFor() => "eng+deu+fra+ita";
}
