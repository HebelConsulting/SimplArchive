namespace SimplArchive.Application.Abstractions;

// Renders a Markdown (.md) file to a PDF for preview — see ADR "CSV and Markdown preview". Converts the
// Markdown to HTML and renders it to PDF (via Gotenberg's Chromium HTML route), so it previews formatted
// (headings, lists, emphasis) in the iframe rather than as raw source.
public interface IMarkdownConverter
{
    // Throws if conversion isn't available/fails; callers fall back to offering no preview.
    Task<byte[]> ConvertToPdfAsync(byte[] markdownBytes, CancellationToken cancellationToken = default);
}
