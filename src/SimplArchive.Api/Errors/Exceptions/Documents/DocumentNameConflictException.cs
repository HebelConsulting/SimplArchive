using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A sibling document already has this name. Thrown from the move (target folder) + create (same parent) paths in
// DocumentsController + RepositoriesController, sharing the DOCUMENT_NAME_CONFLICT wire code; the factories keep
// each site's message.
public sealed class DocumentNameConflictException : DocumentException
{
    private DocumentNameConflictException(string message)
        : base("DOCUMENT_NAME_CONFLICT", StatusCodes.Status409Conflict, message)
    {
    }

    public static DocumentNameConflictException OnTargetFolder() =>
        new("A document with this name already exists under the target folder.");

    public static DocumentNameConflictException OnSameParent() =>
        new("A document with this name already exists under the same parent.");
}
