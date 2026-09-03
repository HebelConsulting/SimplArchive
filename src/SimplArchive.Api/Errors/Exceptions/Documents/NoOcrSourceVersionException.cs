using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// The document has no confirmed, unsigned TIFF or PDF version to apply OCR languages to (#999 — the
/// successor of NO_TIFF_VERSION, widened when the TIFF-only gate fell).
/// </summary>
public sealed class NoOcrSourceVersionException : DocumentException
{
    public NoOcrSourceVersionException()
        : base("NO_OCR_SOURCE_VERSION", StatusCodes.Status400BadRequest,
            "This document has no TIFF or PDF version to apply OCR languages to.")
    {
    }
}
