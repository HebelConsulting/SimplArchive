using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Checkout;

// Thrown when checking out a document that has no confirmed version yet (ADR "Document check-out / check-in").
public sealed class NothingToCheckOutException : CheckoutException
{
    public NothingToCheckOutException()
        : base("NOTHING_TO_CHECK_OUT", StatusCodes.Status400BadRequest, "This document has no confirmed version to check out.")
    {
    }
}
