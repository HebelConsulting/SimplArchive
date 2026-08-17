using NetVips;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Page algebra for the two multi-page formats a scan arrives in: how many pages a file has, and how to take
/// pages out of it, put several together, or put them back in a different order (issue #487).
/// </summary>
/// <remarks>
/// <para>
/// Pure bytes-in/bytes-out — no storage, no HTTP, no naming. That is what lets the operations be tested on
/// real files without a fleet, and it keeps the service above free to worry only about where bytes live and
/// what they are called.
/// </para>
/// <para>
/// PDF goes through <b>PdfPig</b>, which is already a dependency for text layout: it reads, and its
/// <c>PdfDocumentBuilder</c>/<c>PdfMerger</c> also write, so splitting and joining needed no new library and
/// no new licence question. TIFF goes through <b>NetVips</b>, likewise already here for previews — libvips
/// addresses a multi-page TIFF by <c>page</c>/<c>n</c> load options, the same technique the per-page preview
/// renditions use.
/// </para>
/// </remarks>
public static class PageComposer
{
    /// <summary>The formats these operations understand. Anything else is not offered (ADR 0554).</summary>
    public enum PageFormat
    {
        None,
        Pdf,
        Tiff,
    }

    public static PageFormat FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => PageFormat.Pdf,
        ".tif" or ".tiff" => PageFormat.Tiff,
        _ => PageFormat.None,
    };

    public static string ContentTypeOf(PageFormat format) => format switch
    {
        PageFormat.Pdf => "application/pdf",
        PageFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Whether a requested page order (+ rotations) is applicable to a document of <paramref name="pageCount"/>
    /// pages. A subset is allowed (the omitted pages are deleted); a duplicate, an out-of-range page, or an
    /// empty order is not — those are the shapes that mean the caller has made a mistake rather than a choice.
    /// A rotation of a page that is not being kept, or an angle that is not a quarter turn, is likewise a
    /// mistake — never a partial application. Shared by every surface offering page surgery (intray, check-out
    /// working copy), so the rule cannot drift between them.
    /// </summary>
    public static bool IsValidOrder(int pageCount, IReadOnlyList<int> pageOrder, IReadOnlyDictionary<int, int>? rotations) =>
        pageOrder.Count > 0 && pageOrder.Count <= pageCount
        && pageOrder.Distinct().Count() == pageOrder.Count
        && pageOrder.All(p => p >= 1 && p <= pageCount)
        && (rotations is null || rotations.All(r => pageOrder.Contains(r.Key) && r.Value is 90 or 180 or 270));

    /// <summary>
    /// How many pages the file holds. 0 when the bytes cannot be read as the format at all — the caller turns
    /// that into "not offered" rather than a failed operation the user only discovers after clicking.
    /// </summary>
    public static int CountPages(byte[] bytes, PageFormat format)
    {
        try
        {
            switch (format)
            {
                case PageFormat.Pdf:
                    using (var document = PdfDocument.Open(bytes))
                    {
                        return document.NumberOfPages;
                    }

                case PageFormat.Tiff:
                    using (var image = Image.NewFromBuffer(bytes))
                    {
                        return Math.Max(1, Convert.ToInt32(image.Get("n-pages")));
                    }

                default:
                    return 0;
            }
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>One single-page file per page, in page order.</summary>
    public static List<byte[]> Split(byte[] bytes, PageFormat format) =>
        format switch
        {
            PageFormat.Pdf => SplitPdf(bytes),
            PageFormat.Tiff => SplitTiff(bytes),
            _ => [],
        };

    /// <summary>
    /// One file from several, in the order given. The caller owns the order — "join" without a stated order is
    /// a coin flip, so nothing here sorts.
    /// </summary>
    public static byte[] Join(IReadOnlyList<byte[]> sources, PageFormat format) =>
        format switch
        {
            PageFormat.Pdf => PdfMerger.Merge(sources.ToArray()),
            PageFormat.Tiff => JoinTiff(sources),
            _ => [],
        };

    /// <summary>
    /// The same pages, in the order given as 1-based page numbers, each optionally rotated (#522). The order
    /// must be a permutation of the file's pages and every rotation a multiple of 90 — the caller validates
    /// both, because "which pages went missing" is a question the UI has to answer, not this.
    /// </summary>
    /// <remarks>
    /// Rotation honours the format's nature, the same split the straightening path draws (ADR 0575): a PDF
    /// page rotates LOSSLESSLY by composing the /Rotate attribute — re-rasterising to satisfy a rotate would
    /// silently trade the document's real text for an OCR approximation — while a TIFF page has no /Rotate and
    /// is re-encoded, the same trade its deskew already makes, chosen here deliberately rather than inherited.
    /// </remarks>
    public static byte[] Reorder(byte[] bytes, PageFormat format, IReadOnlyList<int> pageOrder, IReadOnlyDictionary<int, int>? rotations = null)
    {
        if (format == PageFormat.Pdf)
        {
            return ReorderPdf(bytes, pageOrder, rotations);
        }

        var pages = Split(bytes, format);
        var picked = pageOrder
            .Select(p => rotations?.GetValueOrDefault(p) is { } degrees and not 0
                ? RotateTiffPage(pages[p - 1], degrees)
                : pages[p - 1])
            .ToList();
        return Join(picked, format);
    }

    // One builder over the source document, not split-then-merge: AddPage copies the page's own content
    // stream and resources, and SetRotation composes with whatever /Rotate the page already carried — a page
    // that arrived at 180 and is turned 90 more ends at 270, not at 90.
    private static byte[] ReorderPdf(byte[] bytes, IReadOnlyList<int> pageOrder, IReadOnlyDictionary<int, int>? rotations)
    {
        using var document = PdfDocument.Open(bytes);
        var builder = new PdfDocumentBuilder();

        foreach (var pageNumber in pageOrder)
        {
            var copied = builder.AddPage(document, pageNumber);
            if (rotations?.GetValueOrDefault(pageNumber) is { } degrees and not 0)
            {
                var existing = document.GetPage(pageNumber).Rotation.Value;
                copied.SetRotation(new PageRotationDegrees((existing + degrees % 360 + 360) % 360));
            }
        }

        return builder.Build();
    }

    private static byte[] RotateTiffPage(byte[] pageBytes, int degrees)
    {
        using var image = Image.NewFromBuffer(pageBytes);
        using var rotated = ((degrees % 360 + 360) % 360) switch
        {
            90 => image.Rot90(),
            180 => image.Rot180(),
            270 => image.Rot270(),
            _ => image.Copy(),
        };
        return rotated.WriteToBuffer(".tif");
    }

    /// <summary>
    /// One file per stretch of pages <b>between</b> the given separator pages, which are themselves discarded
    /// (issue #492). The separators are 1-based page numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty stretches vanish rather than becoming empty documents: two separators in a row, or one at the
    /// very front or back of the batch. That is not a defensive nicety — a person feeding a stack puts a sheet
    /// at the front out of habit, and back-to-back sheets are what happens when one document is pulled out of
    /// the pile at the last moment.
    /// </para>
    /// <para>
    /// A batch with no separators yields the whole thing as one part, which is exactly right: nothing was
    /// asked for, so nothing is cut.
    /// </para>
    /// </remarks>
    public static List<byte[]> CutAt(byte[] bytes, PageFormat format, IReadOnlyList<int> separatorPages)
    {
        var pages = Split(bytes, format);
        var separators = separatorPages.ToHashSet();
        var parts = new List<byte[]>();
        var current = new List<byte[]>();

        for (var page = 1; page <= pages.Count; page++)
        {
            if (separators.Contains(page))
            {
                if (current.Count > 0)
                {
                    parts.Add(Join(current, format));
                    current = [];
                }

                continue;
            }

            current.Add(pages[page - 1]);
        }

        if (current.Count > 0)
        {
            parts.Add(Join(current, format));
        }

        return parts;
    }

    private static List<byte[]> SplitPdf(byte[] bytes)
    {
        var pages = new List<byte[]>();
        using var document = PdfDocument.Open(bytes);

        for (var page = 1; page <= document.NumberOfPages; page++)
        {
            var builder = new PdfDocumentBuilder();
            builder.AddPage(document, page);
            pages.Add(builder.Build());
        }

        return pages;
    }

    // libvips addresses a multi-page TIFF by load options rather than by an API for pages, so one page is one
    // load of the same buffer — the technique the per-page preview renditions already use.
    private static List<byte[]> SplitTiff(byte[] bytes)
    {
        var pages = new List<byte[]>();
        var count = CountPages(bytes, PageFormat.Tiff);

        for (var i = 0; i < count; i++)
        {
            using var page = Image.NewFromBuffer(bytes, kwargs: new VOption { { "page", i }, { "n", 1 } });
            pages.Add(page.WriteToBuffer(".tif"));
        }

        return pages;
    }

    // A multi-page TIFF is written as one tall strip plus a page-height, which is how libvips represents pages
    // (the same shape `n=-1` loads back). One consequence is unavoidable and worth knowing: the pages of a TIFF
    // must share a size, so joining mixed sizes PADS to the largest, and a split-then-join does not round-trip
    // the smaller pages' dimensions.
    //
    // That case is real, not theoretical: a two-page scan out of a commercial DMS held an A4 page (2489x3511)
    // and a receipt strip (667x1846). Padding rather than scaling, because scaling changes what the page IS —
    // a receipt stretched to A4 is a different document, one centred on A4 is the same document on a bigger
    // sheet. No pixels are lost either way, and the source is kept regardless (ADR 0575). PDF has no such
    // constraint, which is one more reason the straightening path's TIFF-to-PDF conversion is not a side effect.
    private static byte[] JoinTiff(IReadOnlyList<byte[]> sources)
    {
        var pages = new List<Image>();
        try
        {
            foreach (var source in sources)
            {
                var count = CountPages(source, PageFormat.Tiff);
                for (var i = 0; i < count; i++)
                {
                    pages.Add(Image.NewFromBuffer(source, kwargs: new VOption { { "page", i }, { "n", 1 } }));
                }
            }

            if (pages.Count == 0)
            {
                return [];
            }

            var width = pages.Max(p => p.Width);
            var height = pages.Max(p => p.Height);
            var normalised = pages
                .Select(p => p.Width == width && p.Height == height ? p : p.Gravity(Enums.CompassDirection.Centre, width, height))
                .ToArray();

            using var joined = Image.Arrayjoin(normalised, across: 1);
            return joined.WriteToBuffer(".tif", new VOption { { "page_height", height } });
        }
        finally
        {
            foreach (var page in pages)
            {
                page.Dispose();
            }
        }
    }
}
