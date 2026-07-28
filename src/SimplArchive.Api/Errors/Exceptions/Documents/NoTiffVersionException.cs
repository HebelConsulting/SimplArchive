using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class NoTiffVersionException : DocumentException
{
    public NoTiffVersionException()
        : base("NO_TIFF_VERSION", StatusCodes.Status400BadRequest, "This document has no TIFF version to apply OCR languages to.")
    {
    }
}
