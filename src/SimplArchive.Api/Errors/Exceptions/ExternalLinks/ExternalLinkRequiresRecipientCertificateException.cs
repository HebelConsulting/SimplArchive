using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// A strict-tier tenant may share outward only as an ENVELOPE, so creating a link means naming who it is
// addressed to (#1377, ADR 0827).
//
// Refused at CREATION rather than at the door, which is the point of having its own exception. The content
// route already refuses to serve plaintext (ExternalLinkCannotServePlaintextException), so without this the
// flow would be: the link is created, it looks live, it appears in the list with an expiry and an access cap,
// the sharer sends it — and the recipient is the one who discovers it was never going to work. A refusal that
// arrives after the artefact has been handed to somebody else is not a refusal, it is a trap.
//
// 409 rather than 400, matching its sibling: the request is well-formed, and what is missing is required by
// the tenant's TIER rather than by the endpoint's shape. A 400 would send the caller looking for a typo.
public sealed class ExternalLinkRequiresRecipientCertificateException : ExternalLinkException
{
    public ExternalLinkRequiresRecipientCertificateException()
        : base("EXTERNAL_LINK_CERTIFICATE_REQUIRED", StatusCodes.Status409Conflict,
            "This tenant is in the strict encryption tier, where a shared document leaves as an envelope "
            + "addressed to a named holder of a private key. Supply the recipient's S/MIME certificate to "
            + "create the link.")
    {
    }
}
