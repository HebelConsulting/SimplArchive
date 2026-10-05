using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The manuals folder's type is assigned by SimplArchive alone, cannot be changed or removed, and is worn by one
// folder per tenant (ADR 0891). Its own wire code: the caller's remedy is to use an ordinary folder.
public sealed class CoreOwnedMaskException : DocumentException
{
    public CoreOwnedMaskException(string message)
        : base("CORE_OWNED_MASK", StatusCodes.Status409Conflict, message)
    {
    }
}
