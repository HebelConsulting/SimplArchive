using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.IntegrationTests;

// The page algebra behind the Inbox's split / join / reorder (issue #487), exercised on REAL bytes — a PDF
// built by PdfPig and a multi-page TIFF built by NetVips, the same two libraries the operations use.
//
// Round-trips rather than fixtures: a test that split a checked-in file and asserted "3 files" would pass with
// an implementation that returned three copies of page 1. Splitting and re-joining, and reordering by a known
// permutation, are the assertions that can actually distinguish right from plausible.
public class PageComposerTests
{
    [Fact]
    public void A_pdf_splits_into_one_file_per_page_and_rejoins_to_the_same_count()
    {
        var original = BuildPdf(5);
        Assert.Equal(5, PageComposer.CountPages(original, PageComposer.PageFormat.Pdf));

        var pages = PageComposer.Split(original, PageComposer.PageFormat.Pdf);

        Assert.Equal(5, pages.Count);
        Assert.All(pages, p => Assert.Equal(1, PageComposer.CountPages(p, PageComposer.PageFormat.Pdf)));

        var rejoined = PageComposer.Join(pages, PageComposer.PageFormat.Pdf);
        Assert.Equal(5, PageComposer.CountPages(rejoined, PageComposer.PageFormat.Pdf));
    }

    [Fact]
    public void A_multi_page_tiff_splits_into_one_file_per_page_and_rejoins_to_the_same_count()
    {
        var original = BuildTiff(4);
        Assert.Equal(4, PageComposer.CountPages(original, PageComposer.PageFormat.Tiff));

        var pages = PageComposer.Split(original, PageComposer.PageFormat.Tiff);

        Assert.Equal(4, pages.Count);
        Assert.All(pages, p => Assert.Equal(1, PageComposer.CountPages(p, PageComposer.PageFormat.Tiff)));

        var rejoined = PageComposer.Join(pages, PageComposer.PageFormat.Tiff);
        Assert.Equal(4, PageComposer.CountPages(rejoined, PageComposer.PageFormat.Tiff));
    }

    // Joining is ordered by the caller and nothing sorts, so joining several files preserves both their order
    // and their own internal page order — a batch of 1 + 2 + 1 pages is a 4-page document.
    [Fact]
    public void Joining_several_files_concatenates_all_their_pages()
    {
        var joined = PageComposer.Join(
            [BuildPdf(1), BuildPdf(2), BuildPdf(1)],
            PageComposer.PageFormat.Pdf);

        Assert.Equal(4, PageComposer.CountPages(joined, PageComposer.PageFormat.Pdf));
    }

    // Reorder keeps the page COUNT and applies the permutation. The count is what a bad implementation loses
    // (dropping or duplicating a page), and it is the only property assertable without rasterising.
    [Theory]
    [InlineData(new[] { 3, 1, 2 })]
    [InlineData(new[] { 3, 2, 1 })]
    [InlineData(new[] { 1, 2, 3 })] // the identity is a permutation too, and must not be a special case
    public void Reorder_keeps_every_page(int[] order)
    {
        var reordered = PageComposer.Reorder(BuildPdf(3), PageComposer.PageFormat.Pdf, order);

        Assert.Equal(3, PageComposer.CountPages(reordered, PageComposer.PageFormat.Pdf));
    }

    [Theory]
    [InlineData("scan.pdf", PageComposer.PageFormat.Pdf)]
    [InlineData("scan.PDF", PageComposer.PageFormat.Pdf)]
    [InlineData("scan.tif", PageComposer.PageFormat.Tiff)]
    [InlineData("scan.TIFF", PageComposer.PageFormat.Tiff)]
    [InlineData("scan.docx", PageComposer.PageFormat.None)]
    [InlineData("scan", PageComposer.PageFormat.None)]
    public void The_format_is_read_from_the_extension(string fileName, PageComposer.PageFormat expected) =>
        Assert.Equal(expected, PageComposer.FormatOf(fileName));

    // Unreadable bytes count as 0 pages rather than throwing: the caller turns that into "not offered", so a
    // corrupt staged file costs the user a missing button, not a failed operation after the click (ADR 0554).
    [Fact]
    public void Bytes_that_are_not_the_format_count_as_no_pages()
    {
        Assert.Equal(0, PageComposer.CountPages([1, 2, 3, 4], PageComposer.PageFormat.Pdf));
        Assert.Equal(0, PageComposer.CountPages([1, 2, 3, 4], PageComposer.PageFormat.Tiff));
    }

    private static byte[] BuildPdf(int pages)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(595, 842); // A4 points
        }

        return builder.Build();
    }

    private static byte[] BuildTiff(int pages)
    {
        var singles = Enumerable.Range(0, pages)
            .Select(i => (NetVips.Image.Black(60, 40) + (i * 20)).Cast(NetVips.Enums.BandFormat.Uchar))
            .ToArray();
        try
        {
            using var joined = NetVips.Image.Arrayjoin(singles, across: 1);
            return joined.WriteToBuffer(".tif", new NetVips.VOption { { "page_height", 40 } });
        }
        finally
        {
            foreach (var single in singles)
            {
                single.Dispose();
            }
        }
    }
}
