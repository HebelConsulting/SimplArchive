using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Thrown when a bulk operation is asked to act on more items than the per-request cap (ADR "Bulk actions on
// selected documents") — a defensive backstop; the clients only ever send the current selection.
public sealed class TooManyBulkItemsException : DocumentException
{
    public TooManyBulkItemsException(int cap)
        : base("TOO_MANY_BULK_ITEMS", StatusCodes.Status400BadRequest, $"A bulk operation may act on at most {cap} items at once.")
    {
    }
}
