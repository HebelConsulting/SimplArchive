using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// Thrown when an upload or generation arrives while a certificate is already set (#1332): the dialog's
// state machine disables both options then — replace goes through delete first, deliberately, so that
// discarding the identity a device already installed is always an explicit act.
public sealed class SmimeCertificateAlreadySetException : EncryptionException
{
    public SmimeCertificateAlreadySetException()
        : base("SMIME_CERTIFICATE_ALREADY_SET", StatusCodes.Status409Conflict,
            "A certificate is already set. Delete it first to upload or generate a new one.")
    {
    }
}
