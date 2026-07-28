using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Search;

// Thrown when a system[Field] filter names a field outside the fixed system-field set (ADR "System-field search").
// The message (listing the known fields) is built by the caller.
public sealed class UnknownSystemFieldException : SearchException
{
    public UnknownSystemFieldException(string message)
        : base("UNKNOWN_SYSTEM_FIELD", StatusCodes.Status400BadRequest, message)
    {
    }
}
