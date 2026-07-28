using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Concurrency;

// Thrown when a mutation on an IConcurrencyTracked resource arrives without the required If-Match header (ADR
// "ETag / If-Match concurrency").
public sealed class IfMatchRequiredException : ConcurrencyException
{
    public IfMatchRequiredException()
        : base("IF_MATCH_REQUIRED", StatusCodes.Status428PreconditionRequired, "The If-Match header is required for this request.")
    {
    }
}
