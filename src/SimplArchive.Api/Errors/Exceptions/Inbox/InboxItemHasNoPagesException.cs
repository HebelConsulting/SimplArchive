using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when a file whose NAME promises pages turns out to have none: a truncated upload, a rename of
// something that was never a PDF, a scan the scanner aborted (#487). Distinct from "not supported" because the
// cause is different — the format is right and the bytes are wrong — and so is the remedy: re-scan or re-upload.
public sealed class InboxItemHasNoPagesException : InboxException
{
    public InboxItemHasNoPagesException(string name)
        : base(
            "INBOX_ITEM_HAS_NO_PAGES",
            StatusCodes.Status400BadRequest,
            $"'{name}' could not be read as a paged document — it may be incomplete or corrupt.")
    {
    }
}
