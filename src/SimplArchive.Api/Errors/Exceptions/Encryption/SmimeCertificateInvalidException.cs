using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// Thrown when an uploaded S/MIME certificate cannot be used (#1332): not parseable as PEM or DER, expired,
// or carrying private-key material where only the public half is accepted.
public sealed class SmimeCertificateInvalidException : EncryptionException
{
    public SmimeCertificateInvalidException(string reason)
        : base("SMIME_CERTIFICATE_INVALID", StatusCodes.Status400BadRequest,
            $"The certificate is not usable: {reason}")
    {
    }
}
