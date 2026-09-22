namespace SimplArchive.UnitTests;

// The OCR sidecar's rotation-confidence threshold (#1232). Guarded here because its failure mode is the
// worst kind: OCRmyPDF's DEFAULT threshold is 14, real-scan Tesseract OSD confidence measures around 11.6,
// so detection ran, produced the correct angle, and silently discarded it — every page came out unrotated
// and everything reported success. The value is code, not prose, so this reads the live line rather than a
// comment about it (the carve-out-verified-a-comment lesson).
public class OcrSidecarConfigTests
{
    [Fact]
    public void The_rotate_confidence_threshold_stays_below_real_scan_confidence()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ocr", "app.py"));
        var match = System.Text.RegularExpressions.Regex.Match(app, @"^ROTATE_CONFIDENCE = ([\d.]+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.True(match.Success, "ocr/app.py no longer defines ROTATE_CONFIDENCE — if the constant moved or "
            + "was renamed, move this guard with it; if it was deleted, the self-disabling default (14) is back.");
        var value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(value < 11.0,
            $"ROTATE_CONFIDENCE is {value}, at or above the ~11.6 confidence real scans actually measure — "
            + "the threshold would discard correct angles again, silently, on every real scan. OCRmyPDF's "
            + "default of 14 is exactly how this feature once disabled itself (#1232).");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
