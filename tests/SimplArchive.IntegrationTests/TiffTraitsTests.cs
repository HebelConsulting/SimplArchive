using NetVips;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.IntegrationTests;

// Whether a TIFF looks like a scan of paper (#491), over REAL files written by NetVips rather than
// hand-crafted byte arrays — the point of the class is to read what actual writers actually emit, and a
// fixture I assembled myself would only prove my parser agrees with my parser.
//
// The rule is deliberately biased toward doing nothing: automatic straightening converts a TIFF to PDF whether
// or not anything was corrected, so a file that does not look like a document must come back false and be left
// exactly as it arrived.
//
// One clause is NOT covered here: BitsPerSample == 1 on its own. libvips would not write that fixture in this
// environment — it refuses bitdepth on anything but 1-band uchar and then silently writes 8 bits, so every
// attempt produced a file that disproved the fixture rather than the parser. The fax test below does exercise a
// genuinely 1-bit file (G4 forces it), but it passes on the compression clause first. Rather than assert
// against a TIFF assembled by hand — which would only prove the parser agrees with itself — the gap is left
// stated: a deflate-compressed bilevel scan is classified by its resolution, not its bit depth.
public class TiffTraitsTests
{
    // The one signal that settles it outright — nothing but a document workflow produces a multi-page TIFF.
    // Note it is asymmetric, which is the whole reason the rest of this class exists: one page proves nothing.
    [Fact]
    public void More_than_one_page_is_a_document_whatever_else_it_looks_like()
    {
        var photoLike = Colour(300, 200, dpi: 72);

        Assert.False(TiffTraits.LooksLikeAScannedDocument(photoLike, pageCount: 1));
        Assert.True(TiffTraits.LooksLikeAScannedDocument(photoLike, pageCount: 3));
    }

    // Fax compression is bilevel-only and effectively never used for photographs, which makes it the strongest
    // single-page signal there is.
    [Fact]
    public void Fax_compression_is_a_document()
    {
        using var page = Image.Black(1200, 1600) + 255;
        var g4 = page.Cast(Enums.BandFormat.Uchar)
            .WriteToBuffer(".tif", new VOption { { "compression", Enums.ForeignTiffCompression.Ccittfax4 }, { "bitdepth", 1 } });

        Assert.True(TiffTraits.LooksLikeAScannedDocument(g4, pageCount: 1));
    }

    // The clause that catches the common case no other signal here does: a COLOUR scan, ordinary compression,
    // one page — indistinguishable from a picture except that someone digitised paper at scanner resolution.
    [Theory]
    [InlineData(300, true)]
    [InlineData(200, true)]
    [InlineData(150, true)]   // exactly the threshold counts as a scan
    [InlineData(96, false)]
    [InlineData(72, false)]   // a screen-resolution picture
    public void Resolution_decides_the_colour_single_page_case(int dpi, bool isDocument) =>
        Assert.Equal(isDocument, TiffTraits.LooksLikeAScannedDocument(Colour(1200, 1600, dpi), pageCount: 1));

    // Anything unreadable is left alone rather than converted on a guess. This is the safe direction: the cost
    // of a false negative is a crooked scan the user can straighten by hand, and of a false positive a file
    // silently turned into a PDF.
    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4 })]              // not a TIFF at all
    [InlineData(new byte[] { 0x49, 0x49 })]              // a TIFF magic and nothing else
    [InlineData(new byte[0])]
    public void Unreadable_bytes_are_not_a_document(byte[] bytes) =>
        Assert.False(TiffTraits.LooksLikeAScannedDocument(bytes, pageCount: 1));

    // A PNG renamed .tif reaches this code the same way anything else does.
    [Fact]
    public void Another_format_is_not_a_document()
    {
        using var image = Image.Black(100, 100) + 255;

        Assert.False(TiffTraits.LooksLikeAScannedDocument(image.WriteToBuffer(".png"), pageCount: 1));
    }

    // vips holds resolution in pixels per millimetre, so a dpi arrives as dpi / 25.4 and comes back out of the
    // file as the inches-based RATIONAL that TiffTraits reads.
    private static byte[] Colour(int width, int height, int dpi)
    {
        using var image = (Image.Black(width, height, bands: 3) + new[] { 200.0, 180.0, 160.0 })
            .Cast(Enums.BandFormat.Uchar)
            .Copy(xres: dpi / 25.4, yres: dpi / 25.4);

        return image.WriteToBuffer(".tif", new VOption { { "compression", Enums.ForeignTiffCompression.Lzw } });
    }
}
