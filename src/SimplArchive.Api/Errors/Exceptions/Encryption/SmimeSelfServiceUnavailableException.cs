using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// Thrown when a self-service certificate mutation arrives on a tenant whose certificates are provisioned
// by the encryption service (#1332): those installations get their identities from an outside source —
// the customer's CA, per-device enrollment (encryption-service ADR 0009) — and the self-service dialog is
// hidden there, so a request reaching this is a non-conforming client, not a user error.
public sealed class SmimeSelfServiceUnavailableException : EncryptionException
{
    public SmimeSelfServiceUnavailableException()
        : base("SMIME_SELF_SERVICE_UNAVAILABLE", StatusCodes.Status409Conflict,
            "S/MIME certificates on this installation are provisioned by the encryption service, not self-service.")
    {
    }
}
