using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Checkout;

// Thrown when extending a check-out on a document that isn't currently checked out (ADR "Self-service check-out
// extension") — there's no idle timer to reset.
public sealed class CheckoutNotHeldException : CheckoutException
{
    public CheckoutNotHeldException()
        : base("CHECKOUT_NOT_HELD", StatusCodes.Status409Conflict, "This document is not currently checked out.")
    {
    }
}
