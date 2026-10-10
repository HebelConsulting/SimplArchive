namespace SimplArchive.Presentation;

/// <summary>How a client shows a preview's bytes.</summary>
public enum PreviewKind
{
    Pdf,
    Image,
    Text,

    /// <summary>No preview: the client offers to open the file in another application.</summary>
    Unsupported,
}

/// <summary>
/// Which way a preview renders, answered identically by the desktop and the phone (ADRs 0650/0651, slice 3 of the
/// mobile app). Magic bytes win over the Content-Type header: stored objects are often served as
/// <c>application/octet-stream</c> (a PDF uploaded by a presigned PUT with no content type), which the header alone
/// misclassifies as unsupported. The web preview sniffs for the same reason (ADR "Web preview pdf.js hit-overlay").
/// </summary>
public static class PreviewKinds
{
    public static PreviewKind Of(byte[] b, string contentType)
    {
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) // "%PDF"
        {
            return PreviewKind.Pdf;
        }

        if (b.Length >= 4 && ((b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) // PNG
                              || (b[0] == 0xFF && b[1] == 0xD8)                               // JPEG
                              || (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)))             // GIF
        {
            return PreviewKind.Image;
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Image;
        }

        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Pdf;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            ? PreviewKind.Text
            : PreviewKind.Unsupported;
    }
}
