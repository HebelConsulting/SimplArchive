using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a split is asked of a single-page file (#487). Splitting it would produce one copy of itself,
// which is not what anyone means by splitting — so it is refused rather than quietly littering the intray.
public sealed class IntrayItemHasNoPagesToSplitException : IntrayException
{
    public IntrayItemHasNoPagesToSplitException(string name)
        : base(
            "INTRAY_ITEM_HAS_NO_PAGES_TO_SPLIT",
            StatusCodes.Status400BadRequest,
            $"'{name}' has only one page, so there is nothing to split.")
    {
    }
}
