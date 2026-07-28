using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Retention;

// A retention extension ("retain until") date was missing, unparseable, or not in the future (ADR "Retention
// review-before-disposition") — extending retention to a past date would be a no-op that leaves the document
// immediately disposable.
public sealed class InvalidRetentionExtensionException : RetentionException
{
    public InvalidRetentionExtensionException()
        : base("INVALID_RETENTION_EXTENSION", StatusCodes.Status400BadRequest,
            "Provide a valid 'retain until' date in the future (yyyy-MM-dd).")
    {
    }
}
