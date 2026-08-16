namespace SimplArchive.Api.Errors.Exceptions.Checkout;

/// <summary>
/// Page operations are only defined for the formats with a page algebra (PDF and TIFF, ADR 0575/0593) — the
/// working copy of this check-out is neither.
/// </summary>
public sealed class CheckoutPagesNotSupportedException : CheckoutException
{
    public CheckoutPagesNotSupportedException()
        : base(
            "CHECKOUT_PAGES_NOT_SUPPORTED",
            StatusCodes.Status400BadRequest,
            "Page operations are only supported for PDF and TIFF working copies.")
    {
    }
}
