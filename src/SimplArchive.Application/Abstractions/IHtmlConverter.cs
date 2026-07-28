namespace SimplArchive.Application.Abstractions;

// Renders an .html/.htm file to a PDF for preview — see ADR "HTML file preview". The HTML is rendered
// server-side (via Gotenberg's Chromium route) to a static PDF, so the file's scripts never run in the
// user's browser and — with an injected CSP — its remote resources don't load. Shown in the iframe PDF
// viewer like the other previews.
public interface IHtmlConverter
{
    // Throws if conversion isn't available/fails; callers fall back to offering no preview.
    Task<byte[]> ConvertToPdfAsync(byte[] htmlBytes, CancellationToken cancellationToken = default);
}
