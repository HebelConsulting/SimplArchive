using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Search;

// Thrown when a filter value is missing or can't be parsed for the target field's data type (ADRs "Typed field
// filters in search" / "System-field search"). The message (the offending value + field/kind) is built by the caller.
public sealed class InvalidFilterValueException : SearchException
{
    public InvalidFilterValueException(string message)
        : base("INVALID_FILTER_VALUE", StatusCodes.Status400BadRequest, message)
    {
    }
}
