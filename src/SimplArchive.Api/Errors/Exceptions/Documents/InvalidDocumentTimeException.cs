using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class InvalidDocumentTimeException : DocumentException
{
    public InvalidDocumentTimeException(string message)
        : base("INVALID_DOCUMENT_TIME", StatusCodes.Status400BadRequest, message)
    {
    }
}
