using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// The Encryption Module is installed but NOT LICENSED for this tenant (#1411, ADR 0859), so nothing can say
// which certificates a reader is addressed by — and on an enveloping tenant that means content cannot be
// served at all.
//
// One of the four refusals ADR 0842 requires to stay distinguishable, and the one whose fix is furthest from
// the reader: a licence is filed by a tenant administrator (ADR 0743), not registered in a profile dialog.
// Collapsed into ContentCannotBeEnvelopedException it read as "your certificate is missing", which sent the
// wrong person to the wrong screen.
//
// 409 rather than 503: unlike a module that threw, this will not recover on its own. The state has to change
// — someone files a licence — which is exactly what 409 says.
public sealed class EncryptionModuleNotLicensedException : EncryptionException
{
    public EncryptionModuleNotLicensedException()
        : base("ENCRYPTION_MODULE_NOT_LICENSED", StatusCodes.Status409Conflict,
            "This tenant serves content only as an envelope, and the encryption module that addresses it is "
            + "not licensed here. Your certificate is not the problem: a tenant administrator needs to file "
            + "a current licence.")
    {
    }
}
