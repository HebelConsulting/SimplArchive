using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Passkeys;

// Thrown when Fido2 rejects the attestation (ADR "WebAuthn / passkeys second factor"). The message carries the
// underlying Fido2 failure detail, so the caller passes it in.
public sealed class PasskeyVerificationFailedException : PasskeyException
{
    public PasskeyVerificationFailedException(string message)
        : base("PASSKEY_VERIFICATION_FAILED", StatusCodes.Status400BadRequest, message)
    {
    }
}
