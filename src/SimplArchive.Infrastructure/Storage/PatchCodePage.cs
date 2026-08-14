using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Draws a <b>Patch 3</b> separator sheet to the real Kodak geometry (issue #492) — the page a user prints and
/// drops between documents in a batch scan.
/// </summary>
/// <remarks>
/// <para>
/// Genuine geometry rather than a marker of our own invention, so that a sheet from an existing scanner is
/// recognised by us and ours is recognised by theirs. The spec, its sources and what it means for a detector
/// are written up in <c>docs/reference/patch-codes.md</c> — the authoritative Kodak document survives only in
/// the Internet Archive, and the freely-summarised versions omit the numbers that matter. From Kodak A-61599:
/// </para>
/// <list type="bullet">
/// <item>wide bars <b>0.20 in</b> (5 mm) ± 0.01;</item>
/// <item>narrow bars <b>and spaces</b> <b>0.08 in</b> (2.03 mm) ± 0.01;</item>
/// <item>four bars separated by three spaces — Patch 3 is <b>W N W N</b>;</item>
/// <item>bars at least <b>2 in</b> long, and the whole code at most <b>0.80 in</b> across;</item>
/// <item>a quiet zone of at least ten narrow bars;</item>
/// <item>bars <b>parallel to the lead edge</b>.</item>
/// </list>
/// <para>
/// The arithmetic self-checks: 0.20 + 0.08 + 0.08 + 0.08 + 0.20 + 0.08 + 0.08 = <b>0.80 in</b>, exactly the
/// stated maximum. That is what confirms the three spaces are narrow-bar width rather than something else.
/// </para>
/// <para>
/// The code is drawn at the top <b>and</b> the bottom, which the spec describes real sheets as doing: a page
/// fed 180° round still presents a code at the lead edge. Left and right edges are deliberately not drawn —
/// bars must be parallel to the lead edge, so a side-fed (landscape) code is a different sheet, not a fourth
/// copy of this one.
/// </para>
/// </remarks>
public static class PatchCodePage
{
    private const double PointsPerInch = 72;

    private const double WideBar = 0.20 * PointsPerInch;    // 14.4 pt
    private const double NarrowBar = 0.08 * PointsPerInch;  // 5.76 pt — spaces are this too
    private const double BarLength = 2.5 * PointsPerInch;   // the spec's minimum is 2 in; a little over is safer
    private const double EdgeMargin = 0.5 * PointsPerInch;  // inside the quiet zone, clear of the feed rollers

    // A4 in points. The sheet is printed by a person on ordinary paper, so the size has to be the one their
    // printer has.
    private const double PageWidth = 595;
    private const double PageHeight = 842;

    /// <summary>Patch 3: wide, narrow, wide, narrow — the bar sequence that means "separate here".</summary>
    private static readonly double[] Patch3Bars = [WideBar, NarrowBar, WideBar, NarrowBar];

    /// <summary>A one-page A4 PDF carrying the Patch 3 code at the top and bottom edges.</summary>
    public static byte[] CreatePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageWidth, PageHeight);
        page.SetTextAndFillColor(0, 0, 0);
        page.SetStrokeColor(0, 0, 0);

        var left = (PageWidth - BarLength) / 2;

        // Top: bars run downward from the margin. Bottom: the mirror, so a sheet fed the other way round still
        // presents a code at whichever edge leads.
        DrawCode(page, left, PageHeight - EdgeMargin, downward: true);
        DrawCode(page, left, EdgeMargin, downward: false);

        return builder.Build();
    }

    private static void DrawCode(PdfPageBuilder page, double left, double startY, bool downward)
    {
        var y = startY;

        foreach (var barHeight in Patch3Bars)
        {
            // PdfPig positions a rectangle by its lower-left corner, so a bar drawn downward starts one bar
            // height below the running edge.
            var bottom = downward ? y - barHeight : y;
            page.DrawRectangle(
                new PdfPoint(left, bottom),
                (decimal)BarLength,
                (decimal)barHeight,
                lineWidth: 0,
                fill: true);

            var step = barHeight + NarrowBar;
            y = downward ? y - step : y + step;
        }
    }
}
