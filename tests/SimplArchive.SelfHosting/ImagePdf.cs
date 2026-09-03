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
