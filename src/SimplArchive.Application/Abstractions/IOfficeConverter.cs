namespace SimplArchive.Application.Abstractions;

// Converts an office / OpenDocument file (docx/xlsx/pptx/odt/ods) to a PDF for preview — see ADR
// "Office document preview via Gotenberg". Implemented in SimplArchive.Infrastructure over the Gotenberg
// sidecar service; the produced PDF is what the browser's built-in viewer renders in the preview iframe.
public interface IOfficeConverter
{
    // fileName must carry the correct extension (e.g. "source.docx") — the converter selects the input
    // filter by extension. Throws if conversion isn't available/fails; callers fall back to the original.
    Task<byte[]> ConvertToPdfAsync(byte[] source, string fileName, CancellationToken cancellationToken = default);
}
