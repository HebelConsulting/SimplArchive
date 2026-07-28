using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class RequiredFieldMissingException : DocumentException
{
    public RequiredFieldMissingException(string message)
        : base("REQUIRED_FIELD_MISSING", StatusCodes.Status400BadRequest, message)
    {
    }
}
