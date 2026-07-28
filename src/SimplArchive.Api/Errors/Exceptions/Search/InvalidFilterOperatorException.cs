using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Search;

// Thrown when a filter operator isn't valid for the target field's data type (ADRs "Typed field filters in
// search" / "System-field search"). The message (naming the operator + field/kind) is built by the caller.
public sealed class InvalidFilterOperatorException : SearchException
{
    public InvalidFilterOperatorException(string message)
        : base("INVALID_FILTER_OPERATOR", StatusCodes.Status400BadRequest, message)
    {
    }
}
