using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class FieldDefinitionNotFoundException : DocumentException
{
    public FieldDefinitionNotFoundException(string message)
        : base("FIELD_DEFINITION_NOT_FOUND", StatusCodes.Status400BadRequest, message)
    {
    }
}
