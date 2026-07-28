using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The referenced document doesn't exist. Two call sites share the DOCUMENT_NOT_FOUND wire code but with different
// intent/status: a legal-hold add treats it as a genuine 404, while filing an inbox item onto a target document
// treats a bad target id as a 400. The factories preserve each.
public sealed class DocumentNotFoundException : DocumentException
{
    private DocumentNotFoundException(int statusCode, string message)
        : base("DOCUMENT_NOT_FOUND", statusCode, message)
    {
    }

    public static DocumentNotFoundException NotFound() =>
        new(StatusCodes.Status404NotFound, "The document was not found.");

    public static DocumentNotFoundException InvalidFilingTarget() =>
        new(StatusCodes.Status400BadRequest, "The target document does not exist.");
}
