using SimplArchive.Infrastructure.Conversion;
using SimplArchive.SelfHosting;

namespace SimplArchive.IntegrationTests;

// The shared PDF builders (#999) must produce what they claim, as judged by the SAME detector the product
// uses — otherwise the pipeline tests built on them would assert against a broken instrument (the
// broken-harness lesson: prove the fixture before trusting what it measures).
public class ImagePdfFixtureTests
{
    [Fact]
    public void The_image_only_fixture_is_a_convertible_scan()
    {
        Assert.Equal(ScannedPdfDetector.ScanVerdict.ConvertibleScan, ScannedPdfDetector.Detect(ImagePdf.ImageOnly()));
    }

    [Fact]
    public void The_text_fixture_is_not_a_scan()
    {
        Assert.Equal(ScannedPdfDetector.ScanVerdict.NotAScan, ScannedPdfDetector.Detect(ImagePdf.TextOnly()));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(14d)]
    [InlineData(90d)]
    [InlineData(180d)]
    public void The_rotated_text_fixture_parses_at_every_angle_the_sidecar_tests_use(double degrees)
    {
        // Judged by the product's own detector, which parses via PdfPig: an unparseable fixture would make
        // the sidecar tests (#1232) assert against a broken instrument. Vector text, so NotAScan is the
        // expected verdict at every angle — the "scan" quality only appears once the sidecar rasterises it.
        Assert.Equal(ScannedPdfDetector.ScanVerdict.NotAScan, ScannedPdfDetector.Detect(ImagePdf.RotatedText(degrees)));
    }

    [Fact]
    public void The_dense_rotated_fixture_parses()
    {
        Assert.Equal(ScannedPdfDetector.ScanVerdict.NotAScan, ScannedPdfDetector.Detect(ImagePdf.DenseRotatedText(3)));
    }
}
