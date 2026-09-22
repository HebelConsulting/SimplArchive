using System.Text;

namespace SimplArchive.SelfHosting;

/// <summary>
/// Builds small, valid PDFs in-process — no binary fixtures (#999). The image-only shape is what a scanner
/// produces (page image, no text layer); the text shape is a born-digital document. Sizes are parameters
/// because the OCR sidecar is a real OCRmyPDF: a token-sized image can fail rasterization, so pipeline
/// tests hand it something page-like.
/// </summary>
public static class ImagePdf
{
    /// <summary>A one-page PDF whose only content is a raw RGB image — an image-only "scan".</summary>
    public static byte[] ImageOnly(int width = 200, int height = 280)
    {
        // White page with a black band: raw 8-bit RGB samples, uncompressed. OCR finds no words in it,
        // which is fine — the pipeline's promise is the SUCCESSOR version, not recognized text.
        var samples = new byte[width * height * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 255;
        }

        for (var y = height / 3; y < height / 3 + 6; y++)
        {
            for (var x = 10; x < width - 10; x++)
            {
                var offset = (y * width + x) * 3;
                samples[offset] = samples[offset + 1] = samples[offset + 2] = 0;
            }
        }

        var image = StreamObject(
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8",
            samples, dictOnlyPrefix: true);
        var contents = StreamObject("<<", Latin1($"q {width} 0 0 {height} 0 0 cm /Im1 Do Q"));

        return Assemble(
        [
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin1($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>"),
            image,
            contents,
        ]);
    }

    /// <summary>A one-page born-digital PDF with real extractable text and no images.</summary>
    public static byte[] TextOnly(string text = "Hello searchable world")
    {
        var contents = StreamObject("<<", Latin1($"BT /F1 12 Tf 40 700 Td ({text}) Tj ET"));
        return Assemble(
        [
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            contents,
        ]);
    }

    /// <summary>
    /// A one-page born-digital PDF whose text block is drawn ROTATED by <paramref name="degrees"/> around
    /// the page centre — several lines, so the raster has the dominant baseline Tesseract's OSD and
    /// Leptonica's deskew both need (#1232). Right angles make a sideways page whose <c>/Rotate</c> is 0
    /// (exactly what the sidecar must detect and fix); a few degrees make a "skewed scan" once rasterised.
    /// Vector text on purpose: the sidecar rasterises internally (Ghostscript / OCRmyPDF), so the raster
    /// carries real glyphs without this repo shipping a bitmap font or a found document — the fixture stays
    /// generated and Apache-2.0 clean, per the issue's licensing note.
    /// </summary>
    public static byte[] RotatedText(double degrees)
    {
        string[] lines =
        [
            "The quick brown fox jumps over the lazy dog.",
            "Pack my box with five dozen liquor jugs today.",
            "How vexingly quick daft zebras jump over fences.",
            "Sphinx of black quartz judge my vow this morning.",
            "The five boxing wizards jump quickly at dawn now.",
            "Bright vixens jump while the dozy fowl quack on.",
            "Quick zephyrs blow vexing daft Jim over the hill.",
            "Two driven jocks help fax my big quiz on Sunday.",
        ];

        const double centreX = 297.5, centreY = 421;
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        // The block is ~400 wide and ~8 lines of 26pt leading tall; start it up-left of centre IN TEXT
        // SPACE so the rotated block stays centred on the page at every angle this repo uses (±15, 90,
        // 180, 270). Tm maps text space through the rotation; T* then advances along the rotated baseline
        // for free, which is what makes every line share one dominant angle.
        const double offsetX = -200, offsetY = 96;
        var startX = centreX + cos * offsetX - sin * offsetY;
        var startY = centreY + sin * offsetX + cos * offsetY;

        var content = new StringBuilder();
        content.Append("BT /F1 16 Tf 26 TL ");
        content.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{cos:0.####} {sin:0.####} {-sin:0.####} {cos:0.####} {startX:0.##} {startY:0.##} Tm "));
        foreach (var line in lines)
        {
            content.Append($"({line}) Tj T* ");
        }

        content.Append("ET");

        var contents = StreamObject("<<", Latin1(content.ToString()));
        return Assemble(
        [
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            contents,
        ]);
    }

    /// <summary>
    /// A DENSE, page-like variant for the deskew tests (#1232): thirty lines, because Leptonica's skew
    /// detection needs a real text page — measured in the sidecar's own container (2026-09-22): a 3°
    /// fixture reads "Deskew angle: 0.000" at one and even eight lines, and "-2.985" at thirty. Each line
    /// carries unique start/end anchor words (start07 … end07), which is what lets a test measure per-line
    /// slope on the OCR text layer without any line-grouping heuristics.
    /// </summary>
    public static byte[] DenseRotatedText(double degrees)
    {
        var lines = Enumerable.Range(1, 30)
            .Select(i => $"start{i:00} the quick brown fox jumps over one lazy dog by the river end{i:00}")
            .ToArray();

        const double centreX = 297.5, centreY = 421;
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        const double offsetX = -230, offsetY = 232;
        var startX = centreX + cos * offsetX - sin * offsetY;
        var startY = centreY + sin * offsetX + cos * offsetY;

        var content = new StringBuilder();
        content.Append("BT /F1 11 Tf 16 TL ");
        content.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{cos:0.####} {sin:0.####} {-sin:0.####} {cos:0.####} {startX:0.##} {startY:0.##} Tm "));
        foreach (var line in lines)
        {
            content.Append($"({line}) Tj T* ");
        }

        content.Append("ET");

        var contents = StreamObject("<<", Latin1(content.ToString()));
        return Assemble(
        [
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            contents,
        ]);
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] StreamObject(string dictPrefix, byte[] streamBytes, bool dictOnlyPrefix = false)
    {
        using var ms = new MemoryStream();
        ms.Write(Latin1(dictPrefix));
        ms.Write(Latin1($" /Length {streamBytes.Length} >>\nstream\n"));
        ms.Write(streamBytes);
        ms.Write(Latin1("\nendstream"));
        return ms.ToArray();
    }

    // Objects 1..n with a valid xref table — the shape PdfPig and OCRmyPDF both accept.
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
