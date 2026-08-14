namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Whether a document carries a digital signature — the one condition under which the inbox leaves a file
/// completely alone (issue #491).
/// </summary>
/// <remarks>
/// <para>
/// <b>Any</b> rewrite voids a signature. A signature covers a byte range of the file, so re-encoding the pages,
/// straightening them, splitting them or merely re-saving the document all invalidate it — and the damage is
/// silent: the file still opens, still looks right, and only announces itself as broken when somebody tries to
/// verify it, possibly years later in front of a court or an auditor. That is why the pipeline refuses the
/// whole document rather than having each processor decide.
/// </para>
/// <para>
/// Detection is a byte scan for the signature dictionary's <c>/ByteRange</c>, which every signed PDF has by
/// construction: it is the array saying which spans of the file the signature covers, and it cannot be
/// compressed away because the signer has to locate it in the raw bytes. TIFF has no standard signature
/// mechanism at all, so a TIFF is never refused on these grounds.
/// </para>
/// <para>
/// The scan can produce a false positive — a document whose text happens to contain the literal string — and
/// that is the acceptable direction: the cost is one file not straightened, against a signature destroyed
/// without anyone noticing. It cannot produce a false NEGATIVE for a real signature, which is the property
/// that matters.
/// </para>
/// </remarks>
public static class DigitalSignature
{
    // Only in PDFs — a signed PDF always carries this in the clear.
    private static readonly byte[] ByteRange = "/ByteRange"u8.ToArray();

    /// <summary>True when the bytes carry a signature that any rewrite would void.</summary>
    public static bool IsSigned(byte[] bytes)
    {
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
        {
            return false; // not a PDF: TIFF and the rest have no in-file signature to void
        }

        return bytes.AsSpan().IndexOf(ByteRange) >= 0;
    }
}
