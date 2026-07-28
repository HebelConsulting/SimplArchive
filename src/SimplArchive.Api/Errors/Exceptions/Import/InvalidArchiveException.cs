using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Import;

// Thrown when the import archive is structurally invalid (ADR "Repository / folder import"). The static factories
// keep each throw site reading intent-first while preserving its specific detail message; all share the
// INVALID_ARCHIVE wire code.
public sealed class InvalidArchiveException : ImportException
{
    private InvalidArchiveException(string message)
        : base("INVALID_ARCHIVE", StatusCodes.Status400BadRequest, message)
    {
    }

    public static InvalidArchiveException MissingManifest() =>
        new("The archive is missing its manifest.");

    public static InvalidArchiveException MissingRoot() =>
        new("The archive root document is missing.");

    public static InvalidArchiveException MissingBlob() =>
        new("A version's blob is missing from the archive.");
}
