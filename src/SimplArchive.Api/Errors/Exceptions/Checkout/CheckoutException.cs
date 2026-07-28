namespace SimplArchive.Api.Errors.Exceptions.Checkout;

// Base class for check-out / check-in errors (ADR "Document check-out / check-in") — both the check-in
// endpoint's own validation and the cross-cutting "this document is checked out by another user" refusal thrown
// at every mutation site. Inherits from ApiException so the global handler translates it to an RFC 7807 response;
// concrete errors inherit from this so a caller can `catch (CheckoutException)`. See the exception-type principle
// in CLAUDE.md.
public abstract class CheckoutException : ApiException
{
    protected CheckoutException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
