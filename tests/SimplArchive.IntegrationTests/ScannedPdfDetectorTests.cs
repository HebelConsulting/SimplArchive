using System.Text;
using SimplArchive.Infrastructure.Conversion;

namespace SimplArchive.IntegrationTests;

// Verifies scanned image-only PDF detection (ADR "Scanned image-only PDF detection"): a PDF with page images
// and no text layer is a convertible scan; a PDF with any extractable text (born-digital / already-OCR'd) or
// no images (vector) is left alone; garbage input is left alone. PDFs are built in-process with valid xref so
// the test carries no binary fixtures. Detection is via PdfPig, so no OCR sidecar is needed.
public class ScannedPdfDetectorTests
{
    [Fact]
    public void Image_only_pdf_is_a_convertible_scan()
    {
        Assert.True(ScannedPdfDetector.IsConvertibleScan(PdfFixtures.ImageOnlyPdf()));
    }

    [Fact]
    public void Text_pdf_is_not_a_scan()
    {
        Assert.False(ScannedPdfDetector.IsConvertibleScan(PdfFixtures.TextPdf()));
    }

    [Fact]
    public void Pdf_with_both_text_and_an_image_is_not_a_scan()
    {
        // Any extractable text excludes it (it's born-digital or already-OCR'd), even with page images.
        Assert.False(ScannedPdfDetector.IsConvertibleScan(PdfFixtures.TextAndImagePdf()));
    }

    [Fact]
    public void Garbage_and_empty_input_are_not_scans()
    {
        Assert.False(ScannedPdfDetector.IsConvertibleScan([]));
        Assert.False(ScannedPdfDetector.IsConvertibleScan(Encoding.ASCII.GetBytes("not a pdf at all")));
    }

    // Builds tiny but structurally-valid PDFs (correct xref offsets) so PdfPig parses them like real files.
    private static class PdfFixtures
    {
        // A 2×2 raw RGB image XObject drawn full-page; no text operators ⇒ GetWords() empty, GetImages() finds it.
        public static byte[] ImageOnlyPdf()
        {
            var image = RawImageObject();
            var content = Latin1("q 300 0 0 200 0 0 cm /Im0 Do Q");
            return Assemble(
            [
                Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
                Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>"),
                Stream(Latin1(""), content),
                image,
            ]);
        }

        // A single line of text drawn with the standard-14 Helvetica ⇒ GetWords() returns words, no images.
        public static byte[] TextPdf()
        {
            var content = Latin1("BT /F1 24 Tf 40 100 Td (Hello scanned world) Tj ET");
            return Assemble(
            [
                Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
                Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
                Stream(Latin1(""), content),
                Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            ]);
        }

        // Both a page image AND text — proves the "any text ⇒ not a scan" rule wins over the image check.
        public static byte[] TextAndImagePdf()
        {
            var image = RawImageObject();
            var content = Latin1("q 300 0 0 200 0 0 cm /Im0 Do Q BT /F1 24 Tf 40 100 Td (Signed original) Tj ET");
            return Assemble(
            [
                Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
                Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /XObject << /Im0 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R >>"),
                Stream(Latin1(""), content),
                image,
                Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            ]);
        }

        private static byte[] RawImageObject()
        {
            // 2×2, 8-bit DeviceRGB, no filter → 12 bytes of raw samples.
            var samples = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
            return Stream(Latin1("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8"), samples, dictOnlyPrefix: true);
        }

        private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

        // Wraps a dictionary + stream bytes into one object body: "<<dict /Length N>>\nstream\n<bytes>\nendstream".
        // When dictOnlyPrefix is true the dict prefix already opens with "<<" and has no trailing ">>".
        private static byte[] Stream(byte[] dictPrefix, byte[] streamBytes, bool dictOnlyPrefix = false)
        {
            using var ms = new MemoryStream();
            var prefix = dictOnlyPrefix ? dictPrefix : Latin1("<<");
            ms.Write(prefix);
            ms.Write(Latin1($" /Length {streamBytes.Length} >>\nstream\n"));
            ms.Write(streamBytes);
            ms.Write(Latin1("\nendstream"));
            return ms.ToArray();
        }

        private static byte[] Assemble(IReadOnlyList<byte[]> bodies)
        {
            using var ms = new MemoryStream();
            void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

            W("%PDF-1.7\n%âãÏÓ\n");

            var offsets = new long[bodies.Count + 1];
            for (var i = 0; i < bodies.Count; i++)
            {
                offsets[i + 1] = ms.Position;
                W($"{i + 1} 0 obj\n");
                ms.Write(bodies[i]);
                W("\nendobj\n");
            }

            var xref = ms.Position;
            var size = bodies.Count + 1;
            W($"xref\n0 {size}\n");
            W("0000000000 65535 f \n");
            for (var i = 1; i < size; i++)
            {
                W($"{offsets[i]:D10} 00000 n \n");
            }

            W($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
            return ms.ToArray();
        }
    }

    // The distinction wave 3 added (#595, ADR 0626): "read it, and it is not a scan" versus "could not read it
    // at all". Both leave the file alone — that is the conservative and correct behaviour — but only one of
    // them means a document the user expected to become searchable silently never will.
    [Fact]
    public void An_unreadable_pdf_is_reported_as_unreadable_not_as_not_a_scan()
    {
        // Bytes that announce themselves as a PDF and then are not one: the shape a truncated upload or a
        // partially-written object takes, which is exactly when this matters.
        var corrupt = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\nthis is not a real pdf body");

        Assert.Equal(ScannedPdfDetector.ScanVerdict.Unreadable, ScannedPdfDetector.Detect(corrupt));

        // The old bool contract is unchanged for every caller that only asks "should I OCR this?".
        Assert.False(ScannedPdfDetector.IsConvertibleScan(corrupt));
    }

    [Fact]
    public void A_readable_non_scan_is_reported_as_not_a_scan()
    {
        // The counterpart assertion, and the one that keeps the verdict honest: a PDF we read successfully and
        // decided against must NOT be reported as unreadable, or the new Warning fires on every ordinary
        // born-digital document and stops meaning anything.
        Assert.Equal(ScannedPdfDetector.ScanVerdict.NotAScan, ScannedPdfDetector.Detect(PdfFixtures.TextPdf()));
        Assert.Equal(ScannedPdfDetector.ScanVerdict.ConvertibleScan, ScannedPdfDetector.Detect(PdfFixtures.ImageOnlyPdf()));
    }
}
