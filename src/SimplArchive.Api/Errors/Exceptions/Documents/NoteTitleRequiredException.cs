using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The title becomes both the document's name and the message's Subject (#564), so an empty one would produce
// a note that is unnameable in the tree AND unidentifiable in a notes client.
public sealed class NoteTitleRequiredException : DocumentException
{
    public NoteTitleRequiredException()
        : base("NOTE_TITLE_REQUIRED", StatusCodes.Status400BadRequest, "A note needs a title.")
    {
    }
}
