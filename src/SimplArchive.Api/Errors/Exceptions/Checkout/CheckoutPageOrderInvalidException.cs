namespace SimplArchive.Api.Errors.Exceptions.Checkout;

/// <summary>
/// The requested page order/rotations cannot be applied to the working copy: empty, a duplicate page, a page
/// out of range, a rotation of a page not being kept, or a non-quarter-turn angle (ADR 0593). Validated as a
/// whole before anything is written — never a partial application.
/// </summary>
public sealed class CheckoutPageOrderInvalidException : CheckoutException
{
    public CheckoutPageOrderInvalidException(int pageCount)
        : base(
            "CHECKOUT_PAGE_ORDER_INVALID",
            StatusCodes.Status400BadRequest,
            $"The requested page order cannot be applied: the working copy has {pageCount} page(s), and the order must name each kept page exactly once (rotations only for kept pages, in quarter turns).")
    {
    }
}
