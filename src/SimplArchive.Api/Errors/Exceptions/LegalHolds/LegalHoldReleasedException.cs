using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.LegalHolds;

// Thrown when adding a document to a hold that has already been released (ADR "Legal hold and retention
// enforcement").
public sealed class LegalHoldReleasedException : LegalHoldException
{
    public LegalHoldReleasedException()
        : base("LEGAL_HOLD_RELEASED", StatusCodes.Status409Conflict, "This legal hold has been released.")
    {
    }
}
