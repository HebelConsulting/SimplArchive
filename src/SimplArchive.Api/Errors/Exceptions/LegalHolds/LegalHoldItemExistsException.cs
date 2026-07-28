using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.LegalHolds;

// Thrown when a document is added to a hold it is already on (ADR "Legal hold and retention enforcement").
public sealed class LegalHoldItemExistsException : LegalHoldException
{
    public LegalHoldItemExistsException()
        : base("LEGAL_HOLD_ITEM_EXISTS", StatusCodes.Status409Conflict, "The document is already on this hold.")
    {
    }
}
