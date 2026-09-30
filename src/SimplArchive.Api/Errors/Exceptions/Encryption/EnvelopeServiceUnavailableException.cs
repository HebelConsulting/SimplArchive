using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// The encryption SERVICE could not decrypt-and-envelope this content (ADR 0862) — it was asked and did not
// answer, or answered a failure.
//
// Its own exception for exactly the reason ADR 0859 gave the sibling above: the fix is not the reader's.
// ContentCannotBeEnvelopedException tells them to "register a current certificate and try again", which is
// wrong and unactionable when the service is down — it sends them to re-do something already correct while
// the fault sits in a log they cannot read.
//
// 503, not 409: a 409 says the state must change before this can succeed, which is true of a missing
// certificate and false of a service that may recover on its own. A client that retries later is behaving
// correctly.
//
// There is deliberately NO plaintext fall-back behind this. On a tenant whose objects are wrapped at rest,
// this process holds neither the data key nor the cleartext (ADR 0862) — so the refusal is not a policy
// choice that could be reconsidered, it is the only thing available. Which is the strongest form the
// guarantee can take.
public sealed class EnvelopeServiceUnavailableException : EncryptionException
{
    public EnvelopeServiceUnavailableException()
        : base("ENVELOPE_SERVICE_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable,
            "This installation could not encrypt your content for delivery, so it was not served. Nothing is "
            + "wrong with your certificate — an administrator needs to look at the encryption service. Try "
            + "again shortly.")
    {
    }
}
