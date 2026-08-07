using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// The requested expiry is in the past, or beyond the tenant's ExternalLinkMaxDays cap (ADR 0546).
public sealed class InvalidExternalLinkExpiryException : ExternalLinkException
{
    public InvalidExternalLinkExpiryException(int maxDays)
        : base("INVALID_EXTERNAL_LINK_EXPIRY", StatusCodes.Status400BadRequest,
            $"An external link must expire in the future and at most {maxDays} days from now.")
    {
    }
}
