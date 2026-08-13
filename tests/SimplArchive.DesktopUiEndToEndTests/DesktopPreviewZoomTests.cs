using Avalonia;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The preview's zoom model (#480, ADR "Fit the whole page"). The desktop had no zoom at all; the interesting
// half is not zooming in but zooming OUT, because 1 is fit-WIDTH — so the scale that shows a whole portrait page
// in a pane wider than it is tall is below 1 and was not expressible.
//
// Pure VM/arithmetic: rasterising a page needs Skia and a bitmap, so the one thing that does — reading the page's
// aspect off the rendered bitmap — is covered by the `--zoom-test` hook end-to-end instead.
public class DesktopPreviewZoomTests
{
    [Fact]
    public void Fit_page_is_below_one_for_a_portrait_page_in_a_pane_wider_than_it_is_tall()
    {
        // A4 portrait in a 900x520 pane — the case in the issue: fitting the width pushes the bottom off screen.
        const double pageBaseWidth = 888, paneHeight = 520;
        var aspect = 842d / 595d;

        Assert.True(pageBaseWidth * aspect > paneHeight, "the page must NOT already fit, or this proves nothing");

        var scale = PreviewZoom.FitPageScale(pageBaseWidth, paneHeight, aspect);

        Assert.NotNull(scale);
        Assert.True(scale < 1, $"fit-page must be below fit-width for a portrait page, was {scale}");
        Assert.True(pageBaseWidth * scale!.Value * aspect <= paneHeight, "the whole page must be inside the pane");
    }

    [Fact]
    public void Fit_page_never_magnifies_a_page_that_already_fits()
    {
        // A landscape page in a tall pane fits by width already. Fit-page shows the whole page; it does not blow
        // a small one up to fill the pane, or the button would mean two different things by document.
        var scale = PreviewZoom.FitPageScale(pageBaseWidth: 400, viewportHeight: 900, aspect: 0.7);

        Assert.Equal(1, scale);
    }

    [Fact]
    public void Fit_page_is_null_until_something_has_been_measured()
    {
        // The panes are user-resizable, so the width is only ever known by measurement — before the first layout
        // there is no scale to compute, and guessing one would jump the page on the next frame.
        Assert.Null(PreviewZoom.FitPageScale(pageBaseWidth: 0, viewportHeight: 520, aspect: 1.4));
        Assert.Null(PreviewZoom.FitPageScale(pageBaseWidth: 888, viewportHeight: 0, aspect: 1.4));
    }

    [Fact]
    public void The_page_is_drawn_at_the_pane_width_times_the_zoom_and_auto_sized_until_measured()
    {
        var vm = new PreviewViewModel();

        // NaN is Avalonia's "Auto": an unmeasured preview lays out as it did before zoom existed.
        Assert.True(double.IsNaN(vm.PageWidth));

        vm.SetViewport(new Size(900, 520));
        Assert.Equal(888, vm.PageWidth);   // 900 less the page item's own 6+6 margin — this IS fit-width

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(888 * 1.25, vm.PageWidth, 3);
    }

    [Fact]
    public void Zoom_stops_at_the_ceiling_and_at_fit_width_until_a_whole_page_has_been_asked_for()
    {
        var vm = new PreviewViewModel();
        vm.SetViewport(new Size(900, 520));

        for (var i = 0; i < 20; i++)
        {
            vm.ZoomInCommand.Execute(null);
        }

        Assert.Equal(4, vm.Zoom, 3);

        // Without a fit-page the floor is fit-width: zooming out below it would show a page smaller than the pane
        // for no reason the user asked for.
        for (var i = 0; i < 20; i++)
        {
            vm.ZoomOutCommand.Execute(null);
        }

        Assert.Equal(1, vm.Zoom, 3);
    }

    [Fact]
    public void A_new_document_opens_at_fit_width_however_the_last_one_was_left()
    {
        var vm = new PreviewViewModel();
        vm.SetViewport(new Size(900, 520));
        vm.ZoomInCommand.Execute(null);
        vm.ZoomInCommand.Execute(null);

        vm.Reset(null); // what RenderAsync does before building the next document's pages

        Assert.Equal(1, vm.Zoom);
    }
}
