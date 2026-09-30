using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// The installation COULD NOT ASK which certificates address this reader (#1411, ADR 0859) — the Encryption
// Module was asked and threw.
//
// Its own exception because the fix is not the reader's. Until now this arrived as
// ContentCannotBeEnvelopedException, which tells them to "register a current certificate and try again" —
// advice that is both wrong and unactionable when the module is broken, and which sends them to a settings
// page to re-do something already correct while the actual fault sits in a log they cannot read.
//
// 503 rather than 409, and the distinction is the message the status itself carries: 409 says the state must
// change before this can succeed, which is true of a missing certificate and false here. A broken module may
// recover on its own, and a client that retries later is behaving correctly.
public sealed class ReaderCertificatesUnavailableException : EncryptionException
{
    public ReaderCertificatesUnavailableException()
        : base("READER_CERTIFICATES_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable,
            "This installation could not determine which certificates your content should be addressed to, "
            + "so it was not served. Nothing is wrong with your certificate — an administrator needs to look "
            + "at the encryption module. Try again shortly.")
    {
    }
}
