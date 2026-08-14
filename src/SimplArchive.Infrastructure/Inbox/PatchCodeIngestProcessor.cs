using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Infrastructure.Inbox;

/// <summary>
/// Cuts an arriving batch scan into one item per document, at the <b>Patch 3 separator sheets</b> somebody put
/// between them (issue #492) — the second step of the ingest pipeline, and deliberately the second.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must run after straightening</b> (ADR 0576). A patch code is horizontal bars read by a projection
/// profile across the page, and two degrees of rotation smears them across scan lines until the profile
/// flattens. Get the order wrong and the failure is silent: a batch that simply does not split, with nothing
/// to explain why.
/// </para>
/// <para>
/// Detection is the OCR sidecar's — it is the only image in the deployment that can rasterise a PDF. The
/// cutting is <see cref="PageComposer"/>'s, which already does exactly this arithmetic for the on-demand split
/// (ADR 0575), so the separator pages are all this needs back.
/// </para>
/// <para>
/// Unlike straightening, this <b>keeps the format</b>: a TIFF batch becomes TIFFs. Nothing is rasterised, so
/// there is nothing to lose, and the item that arrives is the item the user recognises.
/// </para>
/// </remarks>
public sealed class PatchCodeIngestProcessor(
    SimplArchiveDbContext dbContext,
    IPatchCodeDetector detector) : IInboxIngestProcessor
{
    public string Name => "patch-codes";

    public async Task<InboxProcessed?> TryProcessAsync(InboxIngestContext context, CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf(context.Name);
        var kind = format switch
        {
            PageComposer.PageFormat.Pdf => SearchablePdfSourceKind.Pdf,
            PageComposer.PageFormat.Tiff => SearchablePdfSourceKind.Tiff,
            _ => (SearchablePdfSourceKind?)null,
        };

        if (kind is not { } sourceKind)
        {
            return null;
        }

        // The preference is the USER's, and the sweep reads it for items that arrived over WebDAV where no
        // client was involved — which is why it is a column and not a client-side setting.
        var wanted = await dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == context.UserId)
            .Select(u => (bool?)u.CutInboxUploadsAtPatchCodes)
            .FirstOrDefaultAsync(cancellationToken);

        if (wanted != true)
        {
            return null;
        }

        // A single page cannot be a batch: either it is a separator with nothing to separate, or it is one
        // document already. Asking the sidecar would cost a rasterisation to learn nothing.
        if (PageComposer.CountPages(context.Bytes, format) < 2)
        {
            return null;
        }

        var separators = await detector.DetectSeparatorPagesAsync(context.Bytes, sourceKind, cancellationToken);

        // Null is "detection did not run", empty is "no separators in this batch". Both mean leave it alone,
        // and it matters that neither is treated as a cut into one piece — that would rewrite the file for
        // nothing and lose its original name.
        if (separators is not { Count: > 0 })
        {
            return null;
        }

        var parts = PageComposer.CutAt(context.Bytes, format, separators);
        if (parts.Count == 0)
        {
            return null; // a batch of nothing but separator sheets: there is no document in there to keep
        }

        var extension = Path.GetExtension(context.Name);
        var contentType = format == PageComposer.PageFormat.Pdf ? "application/pdf" : "image/tiff";
        return new InboxProcessed(parts.Select(p => new InboxPart(p, extension, contentType)).ToList());
    }
}
