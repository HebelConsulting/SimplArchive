namespace SimplArchive.Application.Abstractions;

// Converts an email file (.eml / .msg) to a PDF for preview — see ADR "Email (.eml/.msg) preview". Parses
// the message (envelope headers + body) and renders it to PDF (via Gotenberg's Chromium HTML route). The
// PDF is what the browser's built-in viewer shows in the preview iframe, consistent with the office formats.
public interface IEmailConverter
{
    // extension selects the parser (".eml" vs ".msg"). Throws if conversion isn't available/fails; callers
    // fall back to offering no preview.
    Task<byte[]> ConvertToPdfAsync(byte[] emailBytes, string extension, CancellationToken cancellationToken = default);
}
