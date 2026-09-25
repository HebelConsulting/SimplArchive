using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// The supplied recipient certificate cannot be read, or cannot encrypt (#1377, ADR 0827).
//
// Checked when the link is CREATED, while the person who pasted it is still there to fix it — the only moment
// at which a bad certificate is cheap. Left to the content route, the same mistake would surface as a share
// that fails for a stranger, days later, with nobody able to say why.
//
// 400, unlike its siblings: this one IS a malformed field. The caller sent something that is not a usable
// certificate, and saying so is actionable in a way that a conflict would not be.
public sealed class InvalidRecipientCertificateException : ExternalLinkException
{
    public InvalidRecipientCertificateException(string reason)
        : base("INVALID_RECIPIENT_CERTIFICATE", StatusCodes.Status400BadRequest,
            $"The recipient certificate could not be used: {reason}. Supply a PEM-encoded X.509 certificate "
            + "whose public key can encrypt (an RSA key-encipherment certificate).")
    {
    }
}
