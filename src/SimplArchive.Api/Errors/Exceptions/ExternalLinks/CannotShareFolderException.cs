using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// A folder has no version to serve, so there is nothing an external link could point at (ADR 0546).
public sealed class CannotShareFolderException : ExternalLinkException
{
    public CannotShareFolderException()
        : base("CANNOT_SHARE_FOLDER", StatusCodes.Status400BadRequest,
            "Only a document can be shared with an external link; a folder has no content to serve.")
    {
    }
}
