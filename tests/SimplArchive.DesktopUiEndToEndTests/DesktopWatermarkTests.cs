using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop watermark logic (ADR "Document watermarking"): PreviewViewModel shows a tiled watermark only when
// a watermark text is set AND there's a preview. Pure VM test — no server needed.
public class DesktopWatermarkTests
{
    [Fact]
    public void Watermark_shows_only_with_text_and_a_preview()
    {
        var pv = new PreviewViewModel(new TestShell());

        // No text → no watermark, even with a preview.
        pv.HasPreviewPages = true;
        Assert.False(pv.HasWatermark);
        Assert.Empty(pv.WatermarkTiles);

        // Text + a preview → the tiled overlay appears.
        pv.WatermarkText = "Confidential · Alice";
        Assert.True(pv.HasWatermark);
        Assert.NotEmpty(pv.WatermarkTiles);
        Assert.All(pv.WatermarkTiles, t => Assert.Equal("Confidential · Alice", t));

        // Text but no preview (placeholder) → no overlay.
        pv.HasPreviewPages = false;
        pv.PreviewText = null;
        Assert.False(pv.HasWatermark);

        // Clearing the text → no watermark.
        pv.PreviewText = "some text";
        Assert.True(pv.HasWatermark);
        pv.WatermarkText = "";
        Assert.False(pv.HasWatermark);
        Assert.Empty(pv.WatermarkTiles);
    }
}
