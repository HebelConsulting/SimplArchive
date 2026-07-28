using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class FieldValueInvalidException : DocumentException
{
    public FieldValueInvalidException(string message)
        : base("FIELD_VALUE_INVALID", StatusCodes.Status400BadRequest, message)
    {
    }
}
