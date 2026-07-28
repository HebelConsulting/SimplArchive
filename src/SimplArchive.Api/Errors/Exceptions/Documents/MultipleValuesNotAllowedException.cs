using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class MultipleValuesNotAllowedException : DocumentException
{
    public MultipleValuesNotAllowedException(string message)
        : base("MULTIPLE_VALUES_NOT_ALLOWED", StatusCodes.Status400BadRequest, message)
    {
    }
}
