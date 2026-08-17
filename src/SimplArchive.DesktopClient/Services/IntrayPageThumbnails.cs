using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// One small picture per page of a staged intray item, for the sort dialog (issue #487).
/// </summary>
/// <remarks>
/// <para>
/// Sorting pages without seeing them is not sorting, it is guessing — so the dialog needs page images, and this
/// is where they come from. The two formats arrive by different routes, for a reason that is not arbitrary:
/// </para>
/// <list type="bullet">
/// <item><b>PDF</b> is rasterised locally by <see cref="PreviewRenderer"/> (PDFium), which the desktop already
/// does for every preview — the bytes are downloaded once and every page comes out of them.</item>
/// <item><b>TIFF</b> uses the server's <c>preview-pages</c> renditions, because an Avalonia <c>Bitmap</c>
/// decodes only the first page of a multi-page TIFF. The Api already produces exactly these images for the
/// preview pane, so this costs no new endpoint.</item>
/// </list>
/// <para>
/// Thumbnails are scaled down here rather than shown full size: a 40-page scan is 40 full-resolution bitmaps
/// held at once, which is a lot of memory for pictures being displayed 140 pixels wide.
/// </para>
/// </remarks>
public static class IntrayPageThumbnails
{
    private const int ThumbnailWidth = 220;

    /// <summary>
    /// The item's pages as bitmaps, in page order. Empty when they cannot be produced — the caller then keeps
    /// the sort affordance hidden rather than opening a dialog full of blanks.
    /// </summary>
    public static async Task<IReadOnlyList<Bitmap>> LoadAsync(
        SimplArchiveApiClient api,
        IntrayApi.IntrayItemInfo item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? await FromPdfAsync(item, cancellationToken)
                : await FromPreviewPagesAsync(api, item, cancellationToken);
        }
        catch (Exception)
        {
            // The dialog is pointless without pictures, so the CALLER treats an empty result as "do not open
            // the dialog" and says so in the status bar — this must not take the whole window down. But it
            // swallows silently, and that has already cost one shipped bug: the WriteableBitmap scaling crash
            // (#522) threw for every PDF page and this catch ate all seven, leaving an empty dialog and no
            // evidence. The --sort-thumbs-test hook exists so the next such failure has somewhere to SHOW
            // itself; when the desktop gains logging (#499), this is a place that must log.
            return [];
        }
    }

    private static async Task<IReadOnlyList<Bitmap>> FromPdfAsync(
        IntrayApi.IntrayItemInfo item,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(item.DownloadUrl, cancellationToken);
        return PreviewRenderer.RenderPdfPages(bytes).Select(Scale).ToList();
    }

    private static async Task<IReadOnlyList<Bitmap>> FromPreviewPagesAsync(
        SimplArchiveApiClient api,
        IntrayApi.IntrayItemInfo item,
        CancellationToken cancellationToken)
    {
        // Follow the item's own preview rel, then the preview-pages rel IT advertises — never a composed path
        // (ADR 0543), and one read per resource rather than one per page (ADR 0557).
        var preview = await api.Intray.GetIntrayPreviewAsync(item, cancellationToken);
        if (preview.PreviewPagesUrl is not { } pagesUrl
            || await api.Versions.GetPreviewPagesAsync(pagesUrl, cancellationToken) is not { } urls)
        {
            return [];
        }

        using var http = new HttpClient();
        var thumbnails = new List<Bitmap>(urls.Count);
        foreach (var url in urls)
        {
            var bytes = await http.GetByteArrayAsync(url, cancellationToken);
            thumbnails.Add(Scale(PreviewRenderer.DecodeImage(bytes)));
        }

        return thumbnails;
    }

    /// <summary>
    /// The check-out working copy's pages (ADR 0593): a PDF rasterises locally from the stash — or the archived
    /// version when no stash exists yet — the same PDFium route as the intray. Anything else gets numbered tiles
    /// (null bitmaps): the checkout preview has no per-page rendition endpoint, and a tile without a picture
    /// still carries its page number, which is enough to rotate a known-upside-down scan.
    /// </summary>
    public static async Task<IReadOnlyList<Bitmap?>> LoadForCheckoutAsync(
        CheckoutClient.CheckoutItem item,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        if (item.FileExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            && (item.StashDownloadUrl ?? item.DownloadUrl) is { } url)
        {
            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(url, cancellationToken);
                return PreviewRenderer.RenderPdfPages(bytes).Select(Scale).Cast<Bitmap?>().ToList();
            }
            catch (Exception)
            {
                // Numbered tiles below — losing the pictures must not cost the dialog (ADR 0575's trade).
            }
        }

        return new Bitmap?[pageCount];
    }

    private static Bitmap Scale(Bitmap source)
    {
        if (source.PixelSize.Width <= ThumbnailWidth)
        {
            return source;
        }

        var scaled = source.CreateScaledBitmap(new Avalonia.PixelSize(
            ThumbnailWidth,
            Math.Max(1, source.PixelSize.Height * ThumbnailWidth / source.PixelSize.Width)));

        source.Dispose();
        return scaled;
    }
}
