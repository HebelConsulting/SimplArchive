using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when a page operation is asked of a file that has no pages to operate on — a .docx, a .msg, an image
// (#487). Only PDF and TIFF carry an addressable page sequence; everything else would have to be converted
// first, which changes what the file IS. A conforming client never sees this: the item's `pages` resource
// advertises no split/sort rel for such a file (ADR 0554). It is the answer to a hand-made request.
public sealed class InboxPagesNotSupportedException : InboxException
{
    public InboxPagesNotSupportedException(string name)
        : base(
            "INBOX_PAGES_NOT_SUPPORTED",
            StatusCodes.Status400BadRequest,
            $"'{name}' is not a format whose pages can be split, joined or sorted — only PDF and TIFF are.")
    {
    }
}
