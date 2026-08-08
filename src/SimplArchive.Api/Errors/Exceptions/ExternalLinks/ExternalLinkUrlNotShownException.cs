using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// The tenant has not opted into revealing an existing link's URL (Tenant.ShowExternalLinkUrl, issue #412).
//
// Distinct from ExternalLinksDisabledException: the feature itself is on and the caller may well hold every
// right to share, so pointing them at the tenant switch for external links, or at a missing permission, would
// send them somewhere that cannot help. This says the one thing that is true — the URL is given out once, at
// creation, and this tenant has not chosen to make it retrievable afterwards.
public sealed class ExternalLinkUrlNotShownException : ExternalLinkException
{
    public ExternalLinkUrlNotShownException()
        : base("EXTERNAL_LINK_URL_NOT_SHOWN", StatusCodes.Status403Forbidden,
            "A link's URL is given out once, when it is created, and this tenant does not make it retrievable afterwards.")
    {
    }
}
