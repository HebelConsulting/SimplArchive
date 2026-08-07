using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// The tenant has AllowExternalLinks switched off (ADR 0546). Deliberately distinct from "you lack the right":
// the feature is off for everyone here, so telling the caller to go and ask for a right would send them down
// the wrong path.
public sealed class ExternalLinksDisabledException : ExternalLinkException
{
    public ExternalLinksDisabledException()
        : base("EXTERNAL_LINKS_DISABLED", StatusCodes.Status403Forbidden,
            "External links are switched off for this tenant.")
    {
    }
}
