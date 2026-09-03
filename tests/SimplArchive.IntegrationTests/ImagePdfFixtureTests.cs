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
}
