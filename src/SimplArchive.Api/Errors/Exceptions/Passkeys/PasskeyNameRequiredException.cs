using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Passkeys;

// Thrown when a passkey is registered without a name (ADR "WebAuthn / passkeys second factor").
public sealed class PasskeyNameRequiredException : PasskeyException
{
    public PasskeyNameRequiredException()
        : base("PASSKEY_NAME_REQUIRED", StatusCodes.Status400BadRequest, "A name for the passkey is required.")
    {
    }
}
