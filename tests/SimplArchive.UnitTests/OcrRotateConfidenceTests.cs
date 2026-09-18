using System.Globalization;
using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The OCR sidecar's orientation threshold stays below what a real scan actually reports (#1232).
//
// THE BUG THIS PINS, and why it was invisible. OCRmyPDF's default confidence threshold is 14. Tesseract OSD
// reports about 11.6 for a genuinely upside-down page of a real 1-bit office scan. So detection ran, produced
// the CORRECT angle, and then discarded it for being insufficiently confident: every upload came out
// unrotated, and every step reported success. Found by hand, in a browser, on real scans.
//
// WHY A THRESHOLD TEST AND NOT AN END-TO-END ONE. The honest end-to-end test drives the sidecar with a
// generated page and asserts the turn was applied — and its verdict would then depend on OSD's confidence for
// a SYNTHETIC page, which is the very quantity that sits near this threshold. A test that can drift across
// the line it is guarding is worse than none: it gets skimmed, then suppressed. This asserts the one number
// whose regression caused the defect, deterministically, and says plainly what it does not cover.
//
// WHAT THIS DOES NOT COVER: that rotation works. It covers that rotation is not switched off. The pipeline
// gap stays open on #1232 with the feasibility findings recorded there.
public class OcrRotateConfidenceTests
{
    // Measured on the shipped sample batch for a genuinely upside-down page of a 1-bit office scan. The number
    // is the evidence, so it is named rather than folded into a comparison — a later reader can re-measure it
    // and see immediately whether this test is still asking the right question.
    private const double MeasuredRealScanConfidence = 11.6;

    [Fact]
    public void The_rotate_threshold_is_below_what_a_real_scan_reports()
    {
        var threshold = ReadThreshold();

        Assert.True(threshold < MeasuredRealScanConfidence,
            $"ROTATE_CONFIDENCE is {threshold.ToString(CultureInfo.InvariantCulture)}, at or above the "
            + $"{MeasuredRealScanConfidence.ToString(CultureInfo.InvariantCulture)} that Tesseract OSD reports "
            + "for a genuinely upside-down page of a real 1-bit office scan. At that setting orientation "
            + "detection RUNS, produces the correct angle, and then throws it away — every upload comes out "
            + "unrotated with every step reporting success. That is how OCRmyPDF's own default of 14 silently "
            + "disabled rotation for this pipeline, and it was found by hand rather than by anything here.");
    }

    [Fact]
    public void The_threshold_is_still_a_guard_rather_than_switched_off_entirely()
    {
        // The other direction, and the reason a bare "smaller is safer" reading is wrong. Zero would accept a
        // confident-but-wrong verdict from anything, and an upside-down page turned the wrong way is worse
        // than one left alone. Upright pages report angle 0 and are never touched; a failed OSD on a blank or
        // sparse page reports angle 0 with confidence 0 — so what remains to guard against is a NON-ZERO angle
        // asserted with no confidence at all.
        Assert.True(ReadThreshold() > 0,
            "ROTATE_CONFIDENCE of 0 accepts any non-zero orientation verdict, however unsupported. A page "
            + "turned the wrong way is a worse outcome than one left alone.");
    }

    // Read from the sidecar's source rather than duplicated here: a copy of the number in this file would let
    // the two drift apart, and the test would then be pinning its own constant rather than the product's.
    private static double ReadThreshold()
    {
        var path = Path.Combine(RepoRoot(), "ocr", "app.py");
        Assert.True(File.Exists(path), $"The OCR sidecar's source is not where this test expects it: {path}");

        var match = Regex.Match(File.ReadAllText(path), @"^ROTATE_CONFIDENCE\s*=\s*([\d.]+)", RegexOptions.Multiline);
        Assert.True(match.Success,
            "ROTATE_CONFIDENCE is no longer a module-level constant in ocr/app.py. If the threshold moved, move "
            + "this test with it — do not delete it: the setting it guards disabled rotation once already.");

        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ocr")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
