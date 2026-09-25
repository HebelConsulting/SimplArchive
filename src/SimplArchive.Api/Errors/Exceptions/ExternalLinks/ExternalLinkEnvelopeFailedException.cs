using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// The link names a recipient certificate, and the envelope could not be built from it (#1377, ADR 0827).
//
// Distinct from ExternalLinkCannotServePlaintextException, which says "no recipient was named". This one says
// "one was named and it no longer works" — a different fix, by a different person: the SENDER re-shares with a
// usable certificate, while the recipient can do nothing at all.
//
// It should be unreachable: the certificate was validated when the link was created. That is exactly why it
// exists rather than a fall-back — the one thing this must never do is serve the plaintext instead, which
// would turn an unreachable branch into a silent hole in the tier's only guarantee. Unreachable code that
// REFUSES costs nothing; unreachable code that degrades is where guarantees go to die.
public sealed class ExternalLinkEnvelopeFailedException : ExternalLinkException
{
    public ExternalLinkEnvelopeFailedException()
        : base("EXTERNAL_LINK_ENVELOPE_FAILED", StatusCodes.Status409Conflict,
            "This link delivers its document as an envelope addressed to a certificate, and that certificate "
            + "can no longer be used. Ask whoever shared it to create a new link with a current certificate.")
    {
    }
}
