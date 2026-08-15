using SimplArchive.Infrastructure.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SimplArchive.IntegrationTests;

/// <summary>
/// The two things we <b>produce</b> for patch-code cutting (issue #492): the printable Patch 3 separator sheet
/// and the sample batch made with it. Detection itself lives in the OCR sidecar and is proven end-to-end by
/// <c>PatchCodeDetectionTests</c>, which needs Docker; these are the halves that can be checked without it.
/// </summary>
/// <remarks>
/// The properties asserted here are the ones a reader of the code cannot verify by eye. The sheet's geometry is
/// a set of constants that look right whatever they say, and a sample whose "upside-down" page is quietly the
/// right way up still opens, still prints, and still looks entirely correct — so both are read back out of the
/// built PDF rather than trusted.
/// </remarks>
public class PatchCodeTests
{
    private const double PointsPerInch = 72;

    [Fact]
    public void The_separator_sheet_is_one_a4_page()
    {
        using var sheet = PdfDocument.Open(PatchCodePage.CreatePdf());

        Assert.Equal(1, sheet.NumberOfPages);
        Assert.Equal(595, sheet.GetPage(1).Width, 1);
        Assert.Equal(842, sheet.GetPage(1).Height, 1);
    }

    /// <summary>
    /// Eight bars: <b>four at the top and four at the bottom</b>, so a sheet fed 180° round still presents a
    /// code at whichever edge leads. Four would mean one of the two codes silently stopped being drawn.
    /// </summary>
    [Fact]
    public void The_separator_sheet_carries_a_code_at_both_feed_edges()
    {
        var bars = Bars();

        Assert.Equal(8, bars.Count);
        Assert.Equal(4, bars.Count(b => b.Bottom > 842 / 2.0));
        Assert.Equal(4, bars.Count(b => b.Bottom < 842 / 2.0));
    }

    /// <summary>
    /// The Kodak A-61599 geometry, measured off the drawn rectangles rather than read from the constants:
    /// wide 0.20 in, narrow 0.08 in, spaces the narrow width, bars at least 2 in long, code at most 0.80 in.
    /// </summary>
    [Fact]
    public void The_separator_sheet_matches_the_published_geometry()
    {
        // Top code only: the bottom one is its mirror, and both are drawn by the same code path.
        var top = Bars().Where(b => b.Bottom > 842 / 2.0).OrderByDescending(b => b.Top).ToList();

        Assert.All(top, bar => Assert.True(
            bar.Width / PointsPerInch >= 2.0,
            $"a bar is {bar.Width / PointsPerInch:0.00} in long; the spec's minimum is 2 in."));

        var heights = top.Select(b => b.Height / PointsPerInch).ToList();
        Assert.Equal(0.20, heights[0], 0.01);
        Assert.Equal(0.08, heights[1], 0.01);
        Assert.Equal(0.20, heights[2], 0.01);
        Assert.Equal(0.08, heights[3], 0.01);

        // The three spaces are a narrow bar wide. This is the clause the freely-available summaries omit, and
        // the arithmetic self-checks: 0.20 + 0.08 x 5 + 0.20 = 0.80 in, exactly the stated maximum width.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0.08, (top[i].Bottom - top[i + 1].Top) / PointsPerInch, 0.01);
        }

        var total = (top[0].Top - top[3].Bottom) / PointsPerInch;
        Assert.Equal(0.80, total, 0.01);
    }

    /// <summary>
    /// The shapes a real stack produces: a sheet at the very front, two back to back where a document was
    /// pulled out at the last moment, and one at the very back. None of them may become an empty document.
    /// </summary>
    [Theory]
    [InlineData(new[] { 1 }, new[] { 4 })]                  // leading separator
    [InlineData(new[] { 5 }, new[] { 4 })]                  // trailing separator
    [InlineData(new[] { 2, 3 }, new[] { 1, 2 })]            // two in a row
    [InlineData(new[] { 1, 2, 3, 4, 5 }, new int[0])]       // nothing but separators
    [InlineData(new int[0], new[] { 5 })]                   // no separators at all: one part, the whole batch
    public void Empty_stretches_between_separators_are_dropped(int[] separators, int[] expectedPageCounts)
    {
        var parts = PageComposer.CutAt(BuildPdf(5), PageComposer.PageFormat.Pdf, separators);

        Assert.Equal(expectedPageCounts, parts.Select(p => PageComposer.CountPages(p, PageComposer.PageFormat.Pdf)));
    }

    // The drawn rectangles, as (left, bottom, width, height) in points. PdfPig exposes a built page's paths, so
    // the sheet can be measured rather than described.
    private static List<(double Left, double Bottom, double Top, double Width, double Height)> Bars()
    {
        using var sheet = PdfDocument.Open(PatchCodePage.CreatePdf());

        return sheet.GetPage(1).ExperimentalAccess.Paths
            .Where(path => path.IsFilled)
            .Select(path => path.GetBoundingRectangle())
            .Where(box => box.HasValue)
            .Select(box => (box!.Value.Left, box.Value.Bottom, box.Value.Top, box.Value.Width, box.Value.Height))
            .ToList();
    }

    private static byte[] BuildPdf(int pages)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(595, 842);
        }

        return builder.Build();
    }
}
