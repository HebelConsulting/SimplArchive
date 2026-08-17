using SimplArchive.Infrastructure.Storage;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.IntegrationTests;

// A digitally signed document is the one thing the intray refuses to touch (#491). Any rewrite voids a
// signature — it covers a byte range, so re-encoding, straightening, splitting or merely re-saving all break
// it, silently: the file still opens and still looks right, and only announces itself as broken when somebody
// tries to verify it.
//
// The direction of the errors is the design. A false positive costs one document not straightened; a false
// negative destroys a signature without anyone noticing. So these tests pin the safe direction explicitly.
public class DigitalSignatureTests
{
    [Fact]
    public void A_pdf_carrying_a_signature_dictionary_is_signed()
    {
        // /ByteRange is what every signed PDF has by construction — the array naming the spans the signature
        // covers. It cannot be compressed away, because the signer has to find it in the raw bytes.
        var signed = Pdf().Concat("\n/Type /Sig /ByteRange [0 840 960 1200]\n"u8.ToArray()).ToArray();

        Assert.True(DigitalSignature.IsSigned(signed));
    }

    [Fact]
    public void An_ordinary_pdf_is_not_signed() => Assert.False(DigitalSignature.IsSigned(Pdf()));

    // TIFF has no standard signature mechanism, so a TIFF is never refused on these grounds — including one
    // whose bytes happen to contain the marker.
    [Fact]
    public void A_non_pdf_is_never_treated_as_signed()
    {
        var tiffish = "II*\0"u8.ToArray().Concat("/ByteRange"u8.ToArray()).ToArray();

        Assert.False(DigitalSignature.IsSigned(tiffish));
        Assert.False(DigitalSignature.IsSigned([1, 2, 3, 4]));
        Assert.False(DigitalSignature.IsSigned([]));
    }

    private static byte[] Pdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(595, 842);
        return builder.Build();
    }
}
