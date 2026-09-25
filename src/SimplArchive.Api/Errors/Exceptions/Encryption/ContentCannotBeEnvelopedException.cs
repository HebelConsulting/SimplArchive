using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// A strict-tier read reached the delivery step and the envelope could not be built (#1393, ADR 0828).
//
// It should be unreachable: the download and preview rels are emitted only when the caller HAS a usable
// certificate, so by the time a request arrives the answer was already yes. That is precisely why this exists
// rather than a fall-back — the one thing this must never do is serve the plaintext instead, which would turn
// an unreachable branch into a silent hole in the tier's only guarantee.
//
// Reachable in one honest way: the certificate was replaced or removed between the resource being read and
// the content being fetched. The message therefore addresses the READER, who is the person who can fix it.
public sealed class ContentCannotBeEnvelopedException : EncryptionException
{
    public ContentCannotBeEnvelopedException()
        : base("CONTENT_CANNOT_BE_ENVELOPED", StatusCodes.Status409Conflict,
            "This tenant serves content only as an envelope addressed to your certificate, and yours could "
            + "not be used. Register a current certificate and try again.")
    {
    }
}
