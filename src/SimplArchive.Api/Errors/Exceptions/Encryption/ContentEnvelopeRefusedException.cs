using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// The encryption service was asked to envelope this content and REFUSED — it answered, and its answer names
// something about what was asked rather than about itself (ADR 0862).
//
// Its own exception beside EnvelopeServiceUnavailableException because the two differ in the one thing a
// client acts on: whether to try again. A 503 says "shortly" and means it; this says retrying is pointless,
// which is the honest answer when the service has told us the request itself is unacceptable — a recipient
// certificate it cannot address, a wrapped DEK that does not belong to the blob.
//
// 500 rather than 409: a 409 belongs to ContentCannotBeEnvelopedException, which tells the reader to register
// a current certificate — actionable when the certificate is missing or unreadable, and wrong here, because
// the reader's certificate may be perfectly valid and the fault ours. The reason the service gave is in the
// Api's log (AtRestKeyService writes it at Error), which is where an administrator can act on it; it is
// deliberately NOT in this message, since a service's prose is not localized and may name internals.
public sealed class ContentEnvelopeRefusedException : EncryptionException
{
    public ContentEnvelopeRefusedException()
        : base("CONTENT_ENVELOPE_REFUSED", StatusCodes.Status500InternalServerError,
            "This installation could not encrypt your content for delivery, so it was not served. Trying "
            + "again will not help — an administrator needs to look at the server log, which names what the "
            + "encryption service refused.")
    {
    }
}
