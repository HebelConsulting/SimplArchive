using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a document is copied into the intray as a template but has no confirmed version to copy (#467).
public sealed class IntraySourceHasNoVersionException : IntrayException
{
    public IntraySourceHasNoVersionException(string documentName)
        : base(
            "INTRAY_SOURCE_HAS_NO_VERSION",
            StatusCodes.Status409Conflict,
            $"'{documentName}' has no version to copy into the intray.")
    {
    }
}
