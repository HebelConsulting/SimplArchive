using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Passkeys;

// Thrown when the attestation response can't be parsed (ADR "WebAuthn / passkeys second factor").
public sealed class PasskeyInvalidException : PasskeyException
{
    public PasskeyInvalidException()
        : base("PASSKEY_INVALID", StatusCodes.Status400BadRequest, "The attestation response is invalid.")
    {
    }
}
