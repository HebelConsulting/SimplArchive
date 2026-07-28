using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Search;

// Thrown when a fields[Name] filter names an index field the tenant doesn't have (ADR "Typed field filters in
// search"). The message (naming the field) is built by the caller.
public sealed class UnknownFilterFieldException : SearchException
{
    public UnknownFilterFieldException(string message)
        : base("UNKNOWN_FILTER_FIELD", StatusCodes.Status400BadRequest, message)
    {
    }
}
