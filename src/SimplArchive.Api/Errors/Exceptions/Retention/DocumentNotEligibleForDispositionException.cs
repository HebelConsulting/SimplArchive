using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Retention;

// A manual disposition was requested for a document that isn't eligible (ADR "Retention review-before-
// disposition"): it has no retention period, isn't yet past its (possibly extended) disposition date, or isn't
// a disposable leaf.
public sealed class DocumentNotEligibleForDispositionException : RetentionException
{
    public DocumentNotEligibleForDispositionException()
        : base("DOCUMENT_NOT_ELIGIBLE_FOR_DISPOSITION", StatusCodes.Status400BadRequest,
            "This document is not currently eligible for disposition.")
    {
    }
}
