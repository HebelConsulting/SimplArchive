using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Import;

// Thrown when the folder an archive is being grafted under doesn't exist (ADR "Repository / folder import").
public sealed class ImportTargetNotFoundException : ImportException
{
    public ImportTargetNotFoundException()
        : base("IMPORT_TARGET_NOT_FOUND", StatusCodes.Status404NotFound, "The import target folder does not exist.")
    {
    }
}
