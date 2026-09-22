using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using SimplArchive.SelfHosting;
using UglyToad.PdfPig;

namespace SimplArchive.UiEndToEndTests;

// Auto-rotate and auto-deskew of scanned uploads, against the REAL sidecar (#1232). Both of this path's
// known bugs were found by hand on real scans because nothing exercised it: the rotate threshold silently
// discarded correct angles (OCRmyPDF's default 14 vs real-scan OSD confidence ~11.6), and --deskew no-ops
// without a dominant baseline. Both failure modes LOOK like success, which is exactly the class a test
// catches and a human reviewing output does not.
//
// Every assertion here is a DIFFERENTIAL — the same input with the feature off and on — because "the call
// succeeded" is precisely the signal these bugs hid behind. The fixtures are generated vector-text pages
// (ImagePdf.RotatedText): the sidecar rasterises internally, so the raster carries real glyphs and a real
// dominant baseline, with no binary fixture and no licensing question.
//
// Like DesktopOcrPipelineTests, this class owns its own container but joins the collection so it
// SERIALIZES with the shared-fixture classes — and it builds only the ocr image (same cached name), not a
// whole stack, because the sidecar's HTTP contract is the thing under test.
[Collection(UiCollection.Name)]
public class OcrSidecarBehaviorTests : IAsyncLifetime
{
    private IContainer? _ocr;
    private string _url = "";

    public async Task InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(new CommonDirectoryPath(SelfHostedApp.RepoRoot()), "ocr")
            .WithDockerfile("Dockerfile")
            .WithName("simplarchive-ocr-capture:latest")   // the SelfHostedApp build's name, so the layer cache is shared
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();

        _ocr = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
            .Build();
        await _ocr.StartAsync();
        _url = $"http://{_ocr.Hostname}:{_ocr.GetMappedPublicPort(8080)}";
    }

    public async Task DisposeAsync()
    {
        if (_ocr is not null)
        {
            await _ocr.DisposeAsync();
        }
    }

    private async Task<byte[]> OcrAsync(byte[] pdf, string query)
    {
        // Generous timeout: the sidecar deliberately serialises heavy work, and a --force-ocr re-render of
        // a full page through Tesseract takes real seconds.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        using var form = new MultipartFormDataContent { { new ByteArrayContent(pdf), "file", "in.pdf" } };
        var response = await http.PostAsync($"{_url}/ocr?kind=pdf&lang=eng{query}", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static int PageRotation(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPage(1).Rotation.Value;
    }

    private static string ExtractedText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPages().Select(p => p.Text));
    }

    [Fact]
    public async Task An_upside_down_page_gets_its_rotation_fixed_losslessly_and_only_when_asked()
    {
        // The PDF path's own rotation (Ghostscript raster → Tesseract OSD → qpdf /Rotate): 180° because its
        // detection is unambiguous where 90/270 depend on a convention this test has no business pinning.
        var input = ImagePdf.RotatedText(180);
        Assert.Equal(0, PageRotation(input));

        var without = await OcrAsync(input, "");
        Assert.Equal(0, PageRotation(without));   // the differential's off-side: nothing asked, nothing turned

        var with = await OcrAsync(input, "&rotate=true");
        Assert.Equal(180, PageRotation(with));

        // LOSSLESS is half the contract (the sidecar's own comment: qpdf only rewrites /Rotate): the
        // original vector text must survive un-rasterised.
        Assert.Contains("quick brown fox", ExtractedText(with), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_sideways_page_reads_only_because_rotation_ran()
    {
        // The force-ocr re-render path (what a TIFF or a Make-searchable override takes): Tesseract cannot
        // read a 90°-turned page, so recognized words are the proof that the orientation pass genuinely ran
        // and was genuinely applied — the exact thing the self-disabled threshold silently stopped.
        var input = ImagePdf.RotatedText(90);

        var without = await OcrAsync(input, "&force=true");
        var with = await OcrAsync(input, "&force=true&rotate=true");

        Assert.DoesNotContain("quick brown fox", ExtractedText(without), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quick brown fox", ExtractedText(with), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_skewed_scans_text_layer_is_coherent_and_level_only_because_deskew_ran()
    {
        // The differential is the OCR text layer's GEOMETRY AND COHERENCE, not word recovery — measured,
        // not preferred: probing the real container (2026-09-22) showed recognition cannot discriminate at
        // any angle (8°+: neither side reads; 3–5°: both do), and that Leptonica only detects skew on a
        // DENSE page (3° reads as "Deskew angle: 0.000" at eight lines, "-2.985" at thirty — the recorded
        // deskew-needs-a-real-text-page lesson, now with numbers). What deskew actually changes at the
        // realistic 1–3° scanner skew is the raster: straightened, the per-line anchors come back as clean
        // LEVEL word pairs; un-deskewed, the tilted raster fragments the layer into single-glyph words
        // (observed live — the anchors stop existing as words at all).
        var input = ImagePdf.DenseRotatedText(3);

        var without = await OcrAsync(input, "&force=true");
        var with = await OcrAsync(input, "&force=true&deskew=true");

        var control = AnchorPairs(without);
        var deskewed = AnchorPairs(with);

        Assert.True(deskewed.Count >= 20,
            $"Only {deskewed.Count}/30 anchor pairs survived WITH deskew — the straightened page should read cleanly.");
        Assert.True(control.Count < deskewed.Count / 2,
            $"The no-deskew control read {control.Count} anchor pairs against {deskewed.Count} with deskew — "
            + "the fixture's tilt no longer degrades the layer, so the assertion below has lost its teeth.");

        var slope = deskewed.Average(p => Math.Abs(p.Slope));
        Assert.True(slope < 0.015,
            $"With deskew requested the text layer still slopes {slope:0.####} on average — the page was not "
            + "straightened. The known shape of this failure is silent: the call succeeds and the output "
            + "looks like the input was already straight (#1232).");
    }

    /// <summary>The per-line (startNN, endNN) anchor pairs that survived OCR as words, with each pair's
    /// rise-over-run — the dense fixture gives every line unique anchors precisely so no line-grouping
    /// heuristic is needed.</summary>
    private static List<(int Line, double Slope)> AnchorPairs(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var words = document.GetPage(1).GetWords().ToList();

        var pairs = new List<(int, double)>();
        for (var i = 1; i <= 30; i++)
        {
            var first = words.FirstOrDefault(w => w.Text.StartsWith($"start{i:00}", StringComparison.OrdinalIgnoreCase));
            var last = words.FirstOrDefault(w => w.Text.StartsWith($"end{i:00}", StringComparison.OrdinalIgnoreCase));
            if (first is null || last is null)
            {
                continue;
            }

            var dx = last.BoundingBox.Centroid.X - first.BoundingBox.Centroid.X;
            var dy = last.BoundingBox.Centroid.Y - first.BoundingBox.Centroid.Y;
            if (Math.Abs(dx) > 100)
            {
                pairs.Add((i, dy / dx));
            }
        }

        return pairs;
    }
}
