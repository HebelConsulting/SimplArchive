using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Concurrency;

// Thrown when a mutation's If-Match ETag is stale — the resource changed since it was read (ADR "ETag / If-Match
// concurrency"). The factories preserve each resource's message; all share the ETAG_MISMATCH wire code.
public sealed class EtagMismatchException : ConcurrencyException
{
    private EtagMismatchException(string message)
        : base("ETAG_MISMATCH", StatusCodes.Status412PreconditionFailed, message)
    {
    }

    public static EtagMismatchException ForDocument() =>
        new("The document has been modified since it was last read.");

    public static EtagMismatchException ForNote() =>
        new("The note has been modified since it was last read.");

    public static EtagMismatchException ForExternalLink() =>
        new("The external link has been modified since it was last read.");
}
