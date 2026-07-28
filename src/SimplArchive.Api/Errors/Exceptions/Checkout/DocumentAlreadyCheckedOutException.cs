using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Checkout;

// Thrown when acquiring a check-out on a document another user already holds (ADR "Document check-out / check-in").
public sealed class DocumentAlreadyCheckedOutException : CheckoutException
{
    public DocumentAlreadyCheckedOutException()
        : base("DOCUMENT_ALREADY_CHECKED_OUT", StatusCodes.Status409Conflict, "This document is already checked out by another user.")
    {
    }
}
