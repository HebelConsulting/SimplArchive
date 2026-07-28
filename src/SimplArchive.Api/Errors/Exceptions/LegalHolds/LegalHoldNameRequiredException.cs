using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.LegalHolds;

// Thrown when a legal hold (matter) is created with a blank name (ADR "Legal hold and retention enforcement").
public sealed class LegalHoldNameRequiredException : LegalHoldException
{
    public LegalHoldNameRequiredException()
        : base("LEGAL_HOLD_NAME_REQUIRED", StatusCodes.Status400BadRequest, "A legal hold name is required.")
    {
    }
}
