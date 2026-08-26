using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The sort dialog's page tiles (issue "the manual's rotate example shows a landscape page in a portrait
// sheet"). Both clients drew every page as the same fixed 130x150 rectangle, which is neither A4 nor a turned
// page — so a dialog whose entire subject is pages was drawing something that is not a page shape, and the one
// tile demonstrating the rotate feature contradicted it.
public class PageTileTests
{
    private const double A4Wide = 2480; // A4 at 300dpi, portrait
    private const double A4Tall = 3508;

    [Fact]
    public void An_upright_page_keeps_its_own_proportions()
    {
        var (width, height) = PageTile.Sheet(A4Wide, A4Tall, 0);

        Assert.Equal(PageTile.BoxHeight, height, 3);           // height binds for a portrait sheet
        Assert.Equal(A4Wide / A4Tall, width / height, 3);      // and the sheet is as narrow as the page is
    }

    [Fact]
    public void A_quarter_turned_page_is_drawn_landscape()
    {
        var upright = PageTile.Sheet(A4Wide, A4Tall, 0);
        var turned = PageTile.Sheet(A4Wide, A4Tall, 90);

        // The whole complaint in one assertion: the sheet has to turn with the page, not stay portrait while
        // the picture inside it turns.
        Assert.True(turned.Width > turned.Height);
        Assert.True(upright.Height > upright.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void A_sheet_never_outgrows_its_cell(int rotation)
    {
        var (width, height) = PageTile.Sheet(A4Wide, A4Tall, rotation);

        Assert.True(width <= PageTile.BoxWidth + 0.001, $"{width} wider than the cell");
        Assert.True(height <= PageTile.BoxHeight + 0.001, $"{height} taller than the cell");
    }

    [Theory]
    [InlineData(0, 180)]      // a half turn is the same shape as none
    [InlineData(90, 270)]
    [InlineData(90, -270)]    // turned left three times, never normalised
    [InlineData(90, 450)]     // and turned right five times
    public void Equivalent_turns_give_the_same_sheet(int expected, int actual)
    {
        Assert.Equal(PageTile.Sheet(A4Wide, A4Tall, expected), PageTile.Sheet(A4Wide, A4Tall, actual));
    }

    // A page whose thumbnail could not be produced still reorders and still turns, so it still has to look like
    // a page — A4 rather than the square a "no size" fallback would otherwise give.
    [Fact]
    public void A_page_with_no_picture_falls_back_to_A4()
    {
        var (width, height) = PageTile.Sheet(0, 0, 0);

        Assert.Equal(PageTile.A4Ratio, width / height, 3);
    }

    [Fact]
    public void The_picture_is_the_sheet_with_its_axes_put_back()
    {
        // Both clients draw the picture unturned and then turn it, so its box is the sheet's, swapped. Getting
        // this wrong is what clipped the turned tile: it was capped by the cell's WIDTH in the direction that
        // had become its height.
        var sheet = PageTile.Sheet(A4Wide, A4Tall, 90);
        var picture = PageTile.Picture(A4Wide, A4Tall, 90);

        Assert.Equal(sheet.Width, picture.Height, 3);
        Assert.Equal(sheet.Height, picture.Width, 3);
        Assert.Equal(PageTile.Sheet(A4Wide, A4Tall, 0), PageTile.Picture(A4Wide, A4Tall, 0));
    }

    [Fact]
    public void A_landscape_page_is_drawn_landscape_without_being_turned()
    {
        // Not every page starts portrait; a page that IS landscape must not be forced into a portrait sheet
        // either, which the fixed rectangle also did.
        var (width, height) = PageTile.Sheet(A4Tall, A4Wide, 0);

        Assert.True(width > height);
        Assert.Equal(PageTile.BoxWidth, width, 3); // width binds for a landscape sheet
    }
}
