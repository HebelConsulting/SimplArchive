namespace SimplArchive.DesktopClient.ViewModels;

// The preview's zoom arithmetic (#480, ADR "Fit the whole page"), stated once and away from the view model so it
// can be checked without a rendered page.
//
// 1 is fit-WIDTH — the page drawn as wide as the pane, which is what the preview has always opened at. It is NOT
// the smallest useful zoom: for a portrait page in a pane wider than it is tall, fitting the width pushes the
// bottom of the page out of view, which is exactly when the user wants to see it AS a page. So the floor is the
// fit-PAGE scale, which is normally below 1, and it stays at 1 until a page has actually been measured.
internal static class PreviewZoom
{
    public const double Max = 4;        // the raster is rendered once, so past ~2x it only gets softer
    public const double FloorMin = 0.1; // a floor below this is a thumbnail, not a page
    public const double Step = 1.25;    // one button press
    public const double WheelStep = 1.1;

    // The page item's own 6+6 horizontal margin, which is not page and must not be zoomed.
    public const double PageMargin = 12;

    // Height held back from the fit so the vertical scrollbar RETRACTS rather than sitting one pixel from
    // needed: its width is part of the viewport, so a page fitted to the exact height can re-trigger the very
    // scrollbar whose disappearance made it fit.
    public const double FitSlack = 14;

    public static double Clamp(double zoom, double floor) => Math.Clamp(zoom, Math.Min(floor, Max), Max);

    // The zoom at which one page's full height fits the viewport, given the width that page is drawn at when
    // zoom is 1. Never above 1: fit-page shows the whole page, it does not magnify a small one to fill the pane.
    // Returns null when nothing has been measured yet.
    public static double? FitPageScale(double pageBaseWidth, double viewportHeight, double aspect)
    {
        if (pageBaseWidth <= 0 || viewportHeight <= 0 || aspect <= 0)
        {
            return null;
        }

        return Math.Clamp((viewportHeight - FitSlack) / (pageBaseWidth * aspect), FloorMin, 1);
    }
}
