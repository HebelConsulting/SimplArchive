using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a sort's page order is not a permutation of the item's pages — a page missing, one listed twice,
// a number out of range (#487). Checked BEFORE anything is written, because the whole reason sorting is allowed
// to replace its source is that a permutation cannot lose a page; a request that would is not a sort.
public sealed class IntrayPageOrderInvalidException : IntrayException
{
    public IntrayPageOrderInvalidException(string name, int pageCount)
        : base(
            "INTRAY_PAGE_ORDER_INVALID",
            StatusCodes.Status400BadRequest,
            $"The page order must list pages of '{name}' (1 to {pageCount}), each at most once, and keep at least one.")
    {
    }
}
