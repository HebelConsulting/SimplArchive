using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// The requested access cap is zero or negative (ADR 0546). Unlimited is expressed as null, not as 0 — a 0 cap
// would mean "a link nobody may open", which is what revoking is for, and reads as a typo for unlimited.
public sealed class InvalidExternalLinkMaxAccessesException : ExternalLinkException
{
    public InvalidExternalLinkMaxAccessesException()
        : base("INVALID_EXTERNAL_LINK_MAX_ACCESSES", StatusCodes.Status400BadRequest,
            "An external link's access limit must be a positive number, or omitted for unlimited.")
    {
    }
}
