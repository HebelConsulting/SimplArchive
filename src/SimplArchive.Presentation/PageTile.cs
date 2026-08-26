namespace SimplArchive.Presentation;

/// <summary>
/// The shape of one page in the sort &amp; rotate dialog: how wide and how tall to draw the sheet, given the
/// page's own proportions and the quarter-turns the user has applied.
/// </summary>
/// <remarks>
/// <para>
/// Both clients drew every page as the same fixed rectangle — 130 by 150, whatever the page actually was. Two
/// things were wrong with that and a reader noticed both. A sheet 130 wide and 150 tall is not A4 (that is
/// 0.87 against 0.71), so a dialog whose whole subject is pages was drawing something that is not a page
/// shape. And turning a page a quarter turn re-fitted the PICTURE inside the frame while the frame stayed
/// portrait, so the one tile demonstrating the rotate feature showed a landscape page in a portrait sheet —
/// the manual's figure for the feature contradicted the feature.
/// </para>
/// <para>
/// It is here rather than in either client because it is arithmetic both must answer identically: what the
/// sheet's proportions are is a property of the page, not of whether it is being drawn into a
/// <c>Border</c> or a <c>div</c>. How it is then painted stays each client's own business.
/// </para>
/// </remarks>
public static class PageTile
{
    /// <summary>The cell a tile is drawn into — its widest and tallest, in device-independent pixels.</summary>
    public const double BoxWidth = 130;

    /// <inheritdoc cref="BoxWidth"/>
    public const double BoxHeight = 150;

    /// <summary>A4's width divided by its height (210 ÷ 297), the fallback when a page's own size is unknown.</summary>
    public const double A4Ratio = 210.0 / 297.0;

    /// <summary>Whether this many degrees is a quarter turn, which swaps a page's axes.</summary>
    /// <param name="rotationDegrees">Clockwise degrees; normalised first, so accumulated or negative turns work.</param>
    public static bool IsQuarterTurn(int rotationDegrees) => ((rotationDegrees % 360) + 360) % 360 is 90 or 270;

    /// <summary>
    /// The sheet to draw: the page's own proportions, turned with it, scaled to fit the cell and never
    /// enlarged past it.
    /// </summary>
    /// <param name="pageWidth">The rendered page's width, in any unit — only the ratio is used.</param>
    /// <param name="pageHeight">The rendered page's height, in the same unit.</param>
    /// <param name="rotationDegrees">Clockwise degrees the user has turned this page by.</param>
    /// <remarks>
    /// A page with no picture — one whose thumbnail could not be produced — falls back to A4 rather than to a
    /// square. It still reorders and still turns, so it still needs to look like a page; an empty sheet that is
    /// page-shaped reads as a page waiting for its picture, while a square one reads as a different kind of
    /// thing.
    /// </remarks>
    public static (double Width, double Height) Sheet(double pageWidth, double pageHeight, int rotationDegrees)
    {
        var ratio = pageWidth > 0 && pageHeight > 0 ? pageWidth / pageHeight : A4Ratio;
        if (IsQuarterTurn(rotationDegrees))
        {
            ratio = 1 / ratio;
        }

        // Fit by whichever side binds — height for a portrait sheet, width for a landscape one.
        var width = BoxHeight * ratio;
        return width <= BoxWidth ? (width, BoxHeight) : (BoxWidth, BoxWidth / ratio);
    }

    /// <summary>The picture's box BEFORE the turn is applied — the sheet with its axes put back.</summary>
    /// <remarks>
    /// Both clients draw the picture unturned and then turn it, so the box it is fitted into is the sheet's,
    /// swapped. Capping both orientations at the same box is what clipped the one tile that demonstrates the
    /// rotate feature.
    /// </remarks>
    public static (double Width, double Height) Picture(double pageWidth, double pageHeight, int rotationDegrees)
    {
        var sheet = Sheet(pageWidth, pageHeight, rotationDegrees);
        return IsQuarterTurn(rotationDegrees) ? (sheet.Height, sheet.Width) : sheet;
    }
}
