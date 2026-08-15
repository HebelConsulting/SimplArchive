using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace SimplArchive.DesktopClient.Services;

// Turns preview bytes into something the Avalonia UI can show. Images decode directly; PDFs (the server's
// rendition for office/email/markdown/html, and pdf originals) are rasterised to a bitmap with PDFium via
// Docnet.Core — which has no SkiaSharp dependency, avoiding a conflict with Avalonia's SkiaSharp. See ADR
// "Desktop workbench UI".
public static class PreviewRenderer
{
    // PDF page render scale (PDFium point size × this). ~2 gives a crisp preview.
    private const int PdfScale = 2;

    public static Bitmap DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    // Rasterises every page of the PDF (the workbench shows them stacked, like a continuous PDF viewer).
    public static List<Bitmap> RenderPdfPages(byte[] pdfBytes)
    {
        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(PdfScale));
        var pageCount = docReader.GetPageCount();

        var pages = new List<Bitmap>(pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            pages.Add(BuildBitmap(pageReader));
        }

        return pages;
    }

    // First page only — used by the headless --render-pdf verification hook.
    public static Bitmap RenderPdfFirstPage(byte[] pdfBytes)
    {
        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(PdfScale));
        using var pageReader = docReader.GetPageReader(0);
        return BuildBitmap(pageReader);
    }

    // Composites the page onto white, so a PDF that draws no page background (most designed documents rely on the
    // viewer's white paper) renders as white paper instead of transparent — otherwise the un-painted areas show
    // through as the dark surface behind the preview (e.g. black bars on a datasheet's margins).
    private static readonly NaiveTransparencyRemover WhiteBackground = new();

    private static Bitmap BuildBitmap(IPageReader pageReader)
    {
        var raw = pageReader.GetImage(WhiteBackground); // BGRA, transparent areas flattened to white
        var width = pageReader.GetPageWidth();
        var height = pageReader.GetPageHeight();

        // An IMMUTABLE Bitmap via the pixel-data constructor, not a WriteableBitmap: Skia's ResizeBitmap
        // supports only immutable sources, so a WriteableBitmap here made CreateScaledBitmap throw "Invalid
        // source bitmap type" for every consumer that scales a page — which is how the sort dialog opened
        // empty for PDFs while its blanket catch ate the evidence (#522). Nothing ever mutated these pages
        // after construction, so writability was cost without benefit.
        var stride = width * 4;
        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(width, height), new Vector(96, 96), stride);
        }
        finally
        {
            handle.Free(); // the Bitmap constructor copies the pixels; the source array is not referenced after
        }
    }
}
