using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Checkout;

// Thrown when checking in a document with no working copy uploaded to the cloud stash yet (ADR "Check-out
// working-copy stash").
public sealed class NoStashException : CheckoutException
{
    public NoStashException()
        : base("NO_STASH", StatusCodes.Status400BadRequest, "Upload the working copy to the cloud before checking in.")
    {
    }
}
