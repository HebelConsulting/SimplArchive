namespace SimplArchive.Api.Errors.Exceptions.Checkout;

/// <summary>
/// The working copy carries a digital signature, which covers a byte range — any rewrite voids it (#491), so
/// page operations are refused rather than silently invalidating what someone signed.
/// </summary>
public sealed class CheckoutWorkingCopySignedException : CheckoutException
{
    public CheckoutWorkingCopySignedException()
        : base(
            "CHECKOUT_WORKING_COPY_SIGNED",
            StatusCodes.Status409Conflict,
            "The working copy is digitally signed; rearranging or rotating its pages would void the signature.")
    {
    }
}
