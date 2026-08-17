using System.Buffers.Binary;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The handful of TIFF tags that say whether a file is plausibly a <b>scanned document</b> rather than a
/// picture — read straight out of the first IFD (issue #491).
/// </summary>
/// <remarks>
/// <para>
/// This exists because automatic straightening is not free for the wrong file. Deskew runs through OCRmyPDF,
/// which only ever emits PDF, so an automatically-processed TIFF <b>changes format whether or not anything was
/// corrected</b>. For a scan that is the point; for a photograph someone dropped in the intray it is a
/// conversion that gains nothing.
/// </para>
/// <para>
/// <b>There is no reliable marker for a single-page document TIFF.</b> The two tags that literally mean it —
/// <c>NewSubfileType</c> bit 1 ("one page of a multi-page image") and <c>PageNumber</c> — are optional, and
/// plenty of writers omit them; libvips writes neither. And the one decisive fact is asymmetric: more than one
/// page proves a document, while one page proves nothing. So this is a judgement from several weak signals,
/// not a flag lookup, and it is deliberately biased toward <b>doing nothing</b>: a file that does not look like
/// a document is left exactly as it arrived.
/// </para>
/// <para>
/// The explicit "straighten this document" action does not consult this at all. There the user has said what
/// they want, and a guess has no business overriding them.
/// </para>
/// <para>
/// Hand-rolled rather than a new dependency: reading five tags out of the first IFD is a page of code over
/// bytes already in memory, where an imaging package would be a licence question and a supply-chain entry for
/// something this small.
/// </para>
/// </remarks>
public static class TiffTraits
{
    // Scanner territory. Below this a file is a screen-resolution picture; at or above it, someone digitised
    // paper — which is why this clause is what catches the common COLOUR scan that no other signal here does.
    private const int ScanDpi = 150;

    private const int TagNewSubfileType = 254;
    private const int TagBitsPerSample = 258;
    private const int TagCompression = 259;
    private const int TagXResolution = 282;
    private const int TagResolutionUnit = 296;
    private const int TagPageNumber = 297;

    private const int CompressionCcittGroup3 = 3;
    private const int CompressionCcittGroup4 = 4;

    /// <summary>
    /// True when the bytes look like a scan of paper. False for anything unreadable, so a file this cannot
    /// parse is left alone rather than converted on a guess.
    /// </summary>
    public static bool LooksLikeAScannedDocument(byte[] bytes, int pageCount)
    {
        // More than one page is the one signal that settles it outright: nothing but a document workflow
        // produces a multi-page TIFF.
        if (pageCount > 1)
        {
            return true;
        }

        var tags = ReadFirstIfd(bytes);
        if (tags.Count == 0)
        {
            return false;
        }

        // Fax compression is bilevel-only and effectively never used for photographs.
        if (tags.TryGetValue(TagCompression, out var compression)
            && compression is CompressionCcittGroup3 or CompressionCcittGroup4)
        {
            return true;
        }

        // One bit per sample is paper, not a picture.
        if (tags.TryGetValue(TagBitsPerSample, out var bits) && bits == 1)
        {
            return true;
        }

        // The tags that mean "a page of a document" when a writer bothers to set them.
        if ((tags.TryGetValue(TagNewSubfileType, out var subfile) && ((int)subfile & 2) != 0)
            || tags.ContainsKey(TagPageNumber))
        {
            return true;
        }

        // ResolutionUnit 2 is inches; 3 is centimetres, where the same threshold in dots-per-cm would be
        // roughly 2.54x smaller. Anything else (or no unit) leaves the resolution uninterpretable, so it is
        // not used to justify converting the file.
        return tags.TryGetValue(TagXResolution, out var dpi)
            && tags.TryGetValue(TagResolutionUnit, out var unit)
            && (unit == 2 ? dpi >= ScanDpi : unit == 3 && dpi >= ScanDpi / 2.54);
    }

    // Tag -> value for the first IFD's SHORT/LONG entries, plus XResolution resolved through its RATIONAL
    // pointer. Returns empty for anything that is not a readable TIFF — the caller reads that as "do nothing".
    private static Dictionary<int, double> ReadFirstIfd(byte[] bytes)
    {
        var tags = new Dictionary<int, double>();
        if (bytes.Length < 8)
        {
            return tags;
        }

        var littleEndian = bytes[0] == 'I' && bytes[1] == 'I';
        if (!littleEndian && !(bytes[0] == 'M' && bytes[1] == 'M'))
        {
            return tags;
        }

        try
        {
            var ifdOffset = (int)ReadUInt32(bytes, 4, littleEndian);
            var entryCount = ReadUInt16(bytes, ifdOffset, littleEndian);

            for (var i = 0; i < entryCount; i++)
            {
                var entry = ifdOffset + 2 + (i * 12);
                var tag = ReadUInt16(bytes, entry, littleEndian);
                var type = ReadUInt16(bytes, entry + 2, littleEndian);
                var count = ReadUInt32(bytes, entry + 4, littleEndian);

                // A tag with more than one value does not hold them inline — the value field is an OFFSET to
                // them. Reading it as a number yields a file position dressed up as data: a real colour scan
                // reported BitsPerSample = 9168, because SamplesPerPixel is 3 and the three eights live
                // elsewhere. Harmless against the `== 1` test below and wrong for anything else, so multi-value
                // tags are skipped rather than half-read.
                if (count > 1 && type != 5)
                {
                    continue;
                }

                // 3 = SHORT and 4 = LONG sit inline in the value field; 5 = RATIONAL is a pointer to two LONGs
                // (numerator, denominator), which is how resolution is always stored.
                tags[tag] = type switch
                {
                    3 => ReadUInt16(bytes, entry + 8, littleEndian),
                    4 => ReadUInt32(bytes, entry + 8, littleEndian),
                    5 => ReadRational(bytes, (int)ReadUInt32(bytes, entry + 8, littleEndian), littleEndian),
                    _ => tags.TryGetValue(tag, out var existing) ? existing : 0,
                };
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A truncated or malformed file: whatever was read before the end is not worth trusting.
            return [];
        }

        return tags;
    }

    private static double ReadRational(byte[] bytes, int offset, bool littleEndian)
    {
        var numerator = ReadUInt32(bytes, offset, littleEndian);
        var denominator = ReadUInt32(bytes, offset + 4, littleEndian);
        return denominator == 0 ? 0 : (double)numerator / denominator;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset, bool littleEndian)
    {
        var span = bytes.AsSpan(offset, 2);
        return littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span);
    }

    private static uint ReadUInt32(byte[] bytes, int offset, bool littleEndian)
    {
        var span = bytes.AsSpan(offset, 4);
        return littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
    }
}
